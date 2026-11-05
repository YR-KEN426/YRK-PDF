# แผน 1.1.6: แก้บั๊กที่พบหลังปล่อย 1.1.5

> ไฟล์นี้แยกจาก `plan.md` เพราะ `plan.md` ตอนนี้เป็นแผน e-Tax รหัสไปรษณีย์ (commit f64ff4e) ห้ามแก้ `plan.md` ในงานนี้
> คำสั่ง Codex ให้ใช้ "อ่าน plan-1.1.6.md แล้วทำขั้นที่ X–Y เท่านั้น …" แทน `plan.md`

## เป้าหมาย
ปล่อย 1.1.6 ให้เร็ว เป็นรุ่นแก้บั๊กอย่างเดียว ไม่มีฟีเจอร์ใหม่ เพราะการเซ็นแบบ LTV ซึ่งเป็นฟีเจอร์หลักใช้ไม่ได้

บั๊กที่ต้องแก้:
1. **เซ็นแบบ LTV ไม่ผ่าน (ร้ายแรงสุด):** `CrlValidation` ปัดตก CRL ที่มีส่วนขยาย Issuing Distribution Point (IDP, 2.5.29.28) แบบ critical แต่ CRL ของ CA ประทับเวลาตัวใหม่ของ DigiCert (`DigiCert Trusted G4 TimeStamping RSA4096 SHA256 2025 CA1`) มี IDP แบบ critical ที่มีแค่ fullName (crl3/crl4) ข้อความผิดพลาดยังบอกว่า "ดาวน์โหลดไม่ได้" ทั้งที่ดาวน์โหลดได้
2. **นโยบายความเป็นส่วนตัวเปิดไม่ได้:** แก้แล้วใน fb0b7c8 (`WithCulture="false"` และ `EmbeddedResourceTests`) ขั้นที่ 5 แค่ตรวจซ้ำ
3. **ดาวน์โหลดอัปเดตค้างไม่จบ:** ถ้าการเชื่อมต่อหยุดกลางทาง โปรแกรมรอไปเรื่อยๆ หน้าต่าง "เกี่ยวกับ" ขึ้นแค่ "กำลังตรวจสอบ…" และกดตรวจซ้ำไม่ได้ ข้อความสถานะยาวเกินช่องจนถูกตัด

นอกขอบเขต (ไว้รุ่นถัดไป): รายการ "ปรับปรุงภายหลัง" ใน `PROJECT_NOTES.md`, fuzz test, เทสต์ส่วน App ชุดใหญ่, e-Tax รหัสไปรษณีย์ (ถ้า Ken ไม่ขอให้รวม)

## ขั้นตอน

### ขั้นที่ 1: เตรียมไฟล์ทดสอบจาก DigiCert จริง (Claude ทำเอง)
- ไฟล์ที่เพิ่ม: `tests/Yrk.PdfStudio.Core.Tests/TestData/DigiCertTsa2025/`
  - `ca1.crl`: จาก http://crl3.digicert.com/DigiCertTrustedG4TimeStampingRSA4096SHA2562025CA1.crl
  - `ca1.cer`: ใบรับรอง CA1 จาก AIA ของ DigiCert (cacerts.digicert.com)
  - `tsa-leaf.cer`: ใบรับรองผู้ประทับเวลา ดึงจากตราประทับเวลาที่ขอจาก `timestamp.digicert.com` ด้วยค่า hash ของข้อมูลสุ่ม (ไม่ส่งข้อมูลของ Ken)
  - `README.md`: ที่มา วันที่ดาวน์โหลด และช่วง thisUpdate/nextUpdate ของ CRL
- ทั้งหมดเป็นข้อมูลสาธารณะ ไม่ใช่ไฟล์ของ Ken เพราะ Codex ใช้เน็ตไม่ได้ Claude จึงต้องดาวน์โหลดให้ก่อน
- ผลที่ต้องได้: ไฟล์ครบ, `openssl` อ่านได้, CRL ใช้ได้ที่เวลาคงที่ในช่วง thisUpdate–nextUpdate
- รุ่น: Claude

### ขั้นที่ 2: ให้ `CrlValidation` ยอมรับ IDP ตาม RFC 5280 (ส่วนความปลอดภัย)
- ไฟล์ที่แก้:
  - `src/Yrk.PdfStudio.Core/Pdf/Signatures/CrlValidation.cs`
  - `tests/Yrk.PdfStudio.Core.Tests/SignatureLtvTests.cs` (หรือไฟล์เทสต์ใหม่ `CrlValidationTests.cs`)
- กติกา: ยอมรับ IDP (2.5.29.28) ที่เป็น critical **เฉพาะเมื่อครบทุกข้อ** ตาม RFC 5280 §5.2.5 และ §6.3.3 (b)
  - มี `distributionPoint` แบบ `fullName` และมีอย่างน้อย 1 URI ที่ตรงกับ URI ใน CRL Distribution Points ของใบรับรองที่ตรวจ (เทียบแบบ `Uri` ไม่สนตัวพิมพ์เล็ก/ใหญ่ของ scheme/host)
  - ไม่มี `onlySomeReasons`, ไม่มี `indirectCRL` = TRUE, ไม่มี `onlyContainsAttributeCerts` = TRUE
  - ถ้ามี `onlyContainsUserCerts` = TRUE ใบรับรองต้องไม่ใช่ CA ถ้ามี `onlyContainsCACerts` = TRUE ใบรับรองต้องเป็น CA (ดูจาก Basic Constraints)
  - อ่าน IDP ด้วย `AsnReader` แบบ DER เต็มโครงสร้าง ถ้ามีส่วนที่ไม่รู้จักหรืออ่านไม่ได้ ให้ปัดตก
  - ส่วนขยาย critical ตัวอื่น, Delta CRL (2.5.29.27), `certificateIssuer` ใน entry ยังปัดตกเหมือนเดิม
  - IDP ที่ไม่มี `distributionPoint` ให้ปัดตก (fail closed)
- `TryCheck` ไม่เปลี่ยน signature: ใช้ `certificate` ที่ส่งมาอ่าน CDP เอง ส่วนตัวตรวจลายเซ็น (`PdfSignatureVerifier`) ได้ผลด้วยโดยไม่ต้องแก้
- เทสต์ที่ต้องมี:
  - CRL จริงของ DigiCert (ขั้นที่ 1) ที่เวลาคงที่: `TryCheck` ได้ true และ `revoked` = false
  - CRL สังเคราะห์ที่สร้างในเทสต์: IDP ตรงกับ CDP → ผ่าน, IDP ชื่อไม่ตรง → ไม่ผ่าน, ใบรับรองไม่มี CDP → ไม่ผ่าน
  - `onlySomeReasons` / `indirectCRL` / `onlyContainsAttributeCerts` → ไม่ผ่าน
  - `onlyContainsUserCerts` กับใบ CA → ไม่ผ่าน, `onlyContainsCACerts` กับใบผู้ใช้ → ไม่ผ่าน
  - IDP ที่ไม่มี `distributionPoint` → ไม่ผ่าน, IDP ซ้ำ 2 ตัว → ไม่ผ่าน, ส่วนขยาย critical ที่ไม่รู้จัก → ยังไม่ผ่าน
  - ตัวตรวจลายเซ็นนับว่า CRL ใน DSS ที่มี IDP ครอบคลุมใบรับรองแบบออฟไลน์ (ไม่ต้องใช้เน็ต)
  - เทสต์ CRL เดิมทั้งหมดใน `SignatureLtvTests` ต้องผ่าน
- รุ่น: Codex `gpt-6.1-sol` · high จากนั้น Claude ตรวจ diff ทุกบรรทัด (เป็นส่วนความปลอดภัย)

### ขั้นที่ 3: ข้อความผิดพลาดตอนเซ็น LTV และแถบ "กำลังเซ็น…"
- ไฟล์ที่แก้:
  - `src/Yrk.PdfStudio.Core/Pdf/Signatures/LongTermValidation.cs`
  - `src/Yrk.PdfStudio.App/ViewModels/MainViewModel.Signatures.cs` และไฟล์ที่มี `TransformToFileAsync` (ถ้าแถบค้างเกิดจากตรงนั้น)
  - `src/Yrk.PdfStudio.App/Localization/th.json`
  - `tests/Yrk.PdfStudio.Core.Tests/SignatureLtvTests.cs`
- ที่ต้องได้:
  - `DownloadAsync` แยก 2 กรณี: ดาวน์โหลดไม่ได้จากทุก URL → ข้อความเดิม, ดาวน์โหลดได้แต่ตรวจไม่ผ่าน → ข้อความใหม่ทำนอง "The revocation list from {0} was downloaded but could not be validated. Sign without long-term validation, or try again later." ทั้งสองแบบเก็บสาเหตุไว้ใน inner exception
  - App บันทึก exception เต็มลง log (`App.LogMessage`) ตอนเซ็นไม่สำเร็จ
  - แถบความคืบหน้าและปุ่มยกเลิกต้องหายก่อนข้อความผิดพลาดขึ้น
  - คำแปลไทยใหม่ใน `th.json` (Ken ตรวจก่อน commit)
- เทสต์: source จำลองที่คืน CRL ที่ตรวจไม่ผ่าน → ได้ข้อความแบบ "ตรวจไม่ผ่าน", source ที่โยน `HttpRequestException` ทุก URL → ได้ข้อความแบบ "ดาวน์โหลดไม่ได้"
- รุ่น: Codex `gpt-6.1-sol` · medium

### ขั้นที่ 4: ดาวน์โหลดอัปเดตค้าง และหน้าต่าง "เกี่ยวกับ"
- ไฟล์ที่แก้:
  - `src/Yrk.PdfStudio.App/Services/AppUpdater.cs`
  - `src/Yrk.PdfStudio.App/ViewModels/MainViewModel.Updates.cs`
  - `src/Yrk.PdfStudio.App/Views/AboutDialog.xaml` และ `AboutDialog.xaml.cs`
  - `src/Yrk.PdfStudio.App/Localization/th.json`
  - เทสต์ใหม่ `tests/Yrk.PdfStudio.Core.Tests/AppUpdaterTests.cs`
- ที่ต้องได้:
  - **เวลาหมดเมื่อไม่มีความคืบหน้า:** `CheckAndDownloadAsync` สร้าง `CancellationTokenSource` ที่ถูกยกเลิกถ้า progress ไม่ขยับเกิน 120 วินาที (นับใหม่ทุกครั้งที่ progress เปลี่ยน) แล้วส่ง token ให้ `DownloadUpdatesAsync` ส่วน `CheckForUpdatesAsync` ใส่เวลาหมดรวม 60 วินาที (`WaitAsync`) ค่าเวลาให้ปรับได้ใน constructor สำหรับเทสต์
  - เมื่อหมดเวลา: ขึ้นข้อความ "The download stopped. Check the internet connection and try again." ไม่เก็บไฟล์ที่ยังไม่ครบ และกดตรวจใหม่ได้ทันทีโดยไม่ต้องปิดโปรแกรม (`s_isCheckingForUpdates` กลับเป็น false)
  - ไม่แตะการตรวจลายเซ็นใน `VerifiedUpdateSource` และไม่เปลี่ยนลำดับ ดาวน์โหลด → ตรวจลายเซ็นทั้งไฟล์ → พร้อมติดตั้ง
  - **หน้าต่าง "เกี่ยวกับ":** แสดง "กำลังดาวน์โหลด… {0}%" ในหน้าต่างเอง และข้อความสถานะขึ้นบรรทัดใหม่ได้ ไม่ถูกตัด
- เทสต์: ตัวดาวน์โหลดจำลองที่หยุดส่งข้อมูลกลางทาง (ตั้งเวลาหมดสั้นในเทสต์) → จบด้วยข้อผิดพลาดภายในเวลาที่กำหนด, ไม่มีไฟล์ค้าง, เรียกซ้ำได้, กรณีปกติยังได้ `UpdateInfo` เหมือนเดิม
- รุ่น: Codex `gpt-6.1-sol` · medium จากนั้น Claude ตรวจ diff ส่วนอัปเดตทุกบรรทัด

### ขั้นที่ 5: ตรวจรวม (Claude)
- รันเทสต์ทั้งชุดนอก sandbox (`dotnet test`) ต้องผ่านทั้งหมด
- Codex ตรวจอีกมุม: `codex exec --sandbox read-only "ตรวจ git diff ตั้งแต่ fb0b7c8~1 หาบั๊ก ช่องโหว่ และสิ่งที่ผิดจาก plan-1.1.6.md"` (เน้น `CrlValidation` และ `AppUpdater`)
- ตั้งเลขรุ่น 1.1.6, เขียน release notes ทั้งไทยและอังกฤษ (`gpt-6-luna` · low แล้ว Claude ตรวจ), อัปเดตวันที่ในนโยบายถ้าแก้นโยบาย
- รุ่น: Claude + Codex read-only

### ขั้นที่ 6: Ken ทดสอบในโปรแกรมจริง (ก่อนปล่อย)
- ติดตั้งตัว build ทดสอบ แล้วลอง:
  - [ ] เซ็นแบบ LTV ตอนเปิดเน็ต กับ Digital ID ทดสอบ: ต้องสำเร็จ และเปิดในแท็บลายเซ็นแล้วแสดงว่ามีข้อมูลตรวจสอบระยะยาว
  - [ ] เปิดไฟล์ที่เซ็นแบบ LTV ตอนปิดเน็ต: สถานะการเพิกถอนต้องไม่ขึ้น "ไม่ทราบ"
  - [ ] เซ็น LTV ตอนปิดเน็ต: ข้อความต้องบอกว่าดาวน์โหลดไม่ได้ และแถบ "กำลังเซ็น…" หายก่อนข้อความขึ้น
  - [ ] ช่วยเหลือ → นโยบายความเป็นส่วนตัว เปิดได้ทั้งไทยและอังกฤษ
  - [ ] หน้าต่างสนับสนุนผู้พัฒนา: ปุ่ม GitHub Sponsors สีชมพูแสดงหัวใจ ♥ ถูกต้อง
  - [ ] อัปเดต 1.1.5 → 1.1.6 จาก feed ทดสอบ ระหว่างดาวน์โหลดลองถอดเน็ต: ภายใน ~2 นาทีต้องขึ้นข้อความและกดลองใหม่ได้ (การแก้นี้มีผลกับการอัปเดตจาก 1.1.6 ขึ้นไป ครั้งนี้ตัวที่ดาวน์โหลดยังเป็นโค้ด 1.1.5)
- จากนั้นปล่อยรุ่นตาม `docs/release-checklist.md` (Ken ยืนยันทุกขั้น)

## คำถามที่ต้องถาม Ken
1. จะรวม e-Tax รหัสไปรษณีย์ (`plan.md`) ใน 1.1.6 หรือไม่ ข้อแนะนำ: ไม่รวม เพื่อให้ปล่อย 1.1.6 ได้เร็ว
2. เวลาหมดตอนดาวน์โหลดหยุด 120 วินาทีพอไหม (เน็ตช้ามากยังขยับทุก 1% ภายในเวลานี้)

## สถานะ
- [x] ขั้นที่ 1: ไฟล์ทดสอบ DigiCert (ตรวจลายเซ็น CRL กับ ca1.cer ด้วย openssl ผ่าน; ไฟล์ยังไม่ได้ตั้งให้ copy ไปโฟลเดอร์ output ของเทสต์ ให้ทำในขั้นที่ 2)
- [ ] ขั้นที่ 2: `CrlValidation` ยอมรับ IDP
- [ ] ขั้นที่ 3: ข้อความผิดพลาด LTV และแถบความคืบหน้า
- [ ] ขั้นที่ 4: ดาวน์โหลดอัปเดตค้าง และหน้าต่าง "เกี่ยวกับ"
- [ ] ขั้นที่ 5: ตรวจรวม เลขรุ่น release notes
- [ ] ขั้นที่ 6: Ken ทดสอบในโปรแกรมจริง และปล่อยรุ่น
