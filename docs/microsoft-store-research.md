# ศึกษา Microsoft Store สำหรับ YRK PDF 2.0.0

ตรวจแหล่งข้อมูลเมื่อ 2026-11-06 · เป็นงานศึกษาอย่างเดียว ยังไม่ได้แก้โค้ด
ข้อที่ยังหาหลักฐานไม่ได้ เขียนกำกับไว้ว่า **ยังไม่ยืนยัน**

## สรุปสั้น

- รุ่น Store ต้อง**ปิด Velopack** แล้วให้ Store อัปเดตเอง เพราะไฟล์ของแอป MSIX แก้ไม่ได้ ส่วนรุ่น GitHub ใช้ Velopack เหมือนเดิม
- ข้อมูลเดิม (ตรายาง, Digital ID, การตั้งค่า) **รุ่น Store มองเห็นได้ทันที** โดยไม่ต้องเขียนโค้ดย้าย แต่ไฟล์ที่สร้าง**ใหม่**ในรุ่น Store จะไปอยู่ในที่ส่วนตัวของแพ็กเกจ และ**ถูกลบเมื่อถอนการติดตั้ง**
- ลิงก์ GitHub Sponsors และ QR พร้อมเพย์ **น่าจะทำได้** เพราะแอปไม่ใช่เกม และผู้บริจาคไม่ได้อะไรตอบแทน แต่ต้องแจ้งใน Partner Center
- Store เซ็น MSIX ให้ฟรี ผู้ติดตั้งจาก Store ไม่เห็นหน้าจอ SmartScreen
- บัญชีนักพัฒนา**ฟรี** ต้องยืนยันตัวตนด้วยบัตรที่ราชการออกและเซลฟี
- **จุดเสี่ยงที่ต้องตัดสินใจ:** ข้อ 10.8.3 และ 10.14 อาจทำให้ต้องใช้บัญชีแบบบริษัท (ดูข้อ 3 และ 6)

---

## 1. Velopack ในรุ่น Store

**คำตอบ: ต้องปิด**

- แอป MSIX ติดตั้งใน `C:\Program Files\WindowsApps\...` ไฟล์เป็นแบบอ่านอย่างเดียว และ Windows จะไม่ให้เปิดแอปถ้าไฟล์ถูกแก้ ("Writes inside the package: Not allowed") Velopack จึงเขียนไฟล์รุ่นใหม่ทับไม่ได้
  ที่มา: https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes (หน้าแก้ล่าสุด 2026-09-30)
- กฎ 10.2.5 ที่บังคับว่า "ติดตั้งและอัปเดตผ่าน Store เท่านั้น" เขียนไว้สำหรับเกมและ Xbox แต่ข้อจำกัดทางเทคนิคด้านบนก็ทำให้ต้องปิดอยู่ดี
  ที่มา: https://learn.microsoft.com/en-us/windows/apps/publish/store-policies (Store Policies 7.20, หน้าลงวันที่ 2026-09-14)
- Store จะอัปเดตแพ็กเกจให้เอง เมื่อ Ken ส่งรุ่นใหม่ผ่าน Partner Center และผ่านการตรวจแล้ว
- Velopack ไม่มีเอกสารหรือ issue เรื่อง MSIX/Store ที่ค้นเจอ (ค้นที่ docs.velopack.io และ issue บน GitHub) **ยังไม่ยืนยัน**ว่าใน MSIX `UpdateManager.IsInstalled` จะเป็น false เอง

**สิ่งที่โค้ดทำอยู่แล้ว:** `AppUpdater.IsAvailable` คืนค่า false เมื่อ `IsInstalled` เป็น false (`src/Yrk.PdfStudio.App/Services/AppUpdater.cs:67`) และถ้าไม่มี feed URL ก็จะไม่สร้างตัวอัปเดตเลย (`AppUpdater.cs:35`)

**วิธีทำให้มีทั้งสองรุ่นจากโค้ดชุดเดียว (แนะนำทำทั้งสองชั้น)**
1. **ตอน build:** เพิ่มค่า MSBuild เช่น `-p:YrkStore=true`
   - ไม่ใส่ `YrkUpdateUrl` ใน assembly metadata ตัวอัปเดตก็จะไม่ทำงาน
   - กำหนด constant `YRK_STORE` เพื่อซ่อนเมนู "ตรวจหาการอัปเดต" และเปลี่ยนข้อความในหน้าต่างเกี่ยวกับ
2. **ตอนรัน (กันพลาด):** เรียก `GetCurrentPackageFullName` ของ Windows
   - ถ้าคืน `APPMODEL_ERROR_NO_PACKAGE` แปลว่าไม่ได้รันจาก MSIX
   - ถ้ามี package identity ให้ปิด Velopack ทุกกรณี แม้ build ผิด flag ก็ไม่พัง
   - ที่มา: https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getcurrentpackagefullname
3. ห้ามเปลี่ยน package id `YRK.EasyPdf`, `YrkEasyPdf.exe` และโฟลเดอร์ข้อมูลของรุ่น GitHub (ตามกฎเดิม) ชื่อแพ็กเกจใน Store เป็นชื่อแยกที่ Partner Center ออกให้ ไม่ชนกัน

ส่วนแอปอื่นที่มีทั้งตัวติดตั้งและรุ่น Store ก็ใช้แนวทางเดียวกันนี้ (เช่น EmEditor มีหน้า FAQ แยกรุ่น Store กับตัวติดตั้ง) แต่ไม่ได้ตรวจโค้ดของแอปเหล่านั้น: **ยังไม่ยืนยัน**ว่าแต่ละแอปตรวจด้วยวิธีไหน

## 2. ที่เก็บข้อมูลใน MSIX และการย้ายของผู้ใช้เดิม

**โฟลเดอร์ข้อมูลปัจจุบัน (จากโค้ด):** `%LocalAppData%\YRK\EasyPdf\`

| ข้อมูล | ที่อยู่ | ไฟล์โค้ด |
|---|---|---|
| ตั้งค่าทั่วไป | `settings.json` | `Services/AppUpdater.cs:182` |
| ตรายาง | `stamps.json` | `Services/StampSettings.cs:12` |
| Digital ID | `DigitalIDs\` | `Services/DigitalIdStore.cs:32` |
| ตั้งค่าลายเซ็นดิจิทัล | `digital-sign.json` | `Services/DigitalSignSettings.cs:17` |
| ลายเซ็นรูปภาพ | `Signatures\` | `Services/SignatureStore.cs:37` |
| e-Tax | `etax.json` | `Services/ETaxSettings.cs:12` |
| ไฟล์ล่าสุด | `recent.json` | `Services/RecentFiles.cs:25` |
| กู้คืนไฟล์ | `Recovery\` | `Services/RecoveryStore.cs:34` |
| ภาษา OCR | `tessdata\` | `Core/Pdf/Ocr/OcrLanguageData.cs:45` |
| log | `logs\` | `App.xaml.cs:100` |

(ไฟล์ทั้งหมดอยู่ใต้ `src/Yrk.PdfStudio.App/` ยกเว้น OCR ที่อยู่ใน Core) ส่วนใบรับรองใน Windows certificate store (`SignatureStore.cs:48`) ไม่ได้อยู่ในโฟลเดอร์นี้

**MSIX ทำอะไรกับ AppData (Windows 10 1903 ขึ้นไป และ Windows 11)**
- ไฟล์หรือโฟลเดอร์ที่**สร้างใหม่**ใต้ `AppData\Local` จะถูกย้ายไปเก็บในที่ส่วนตัวของแพ็กเกจ (`%LocalAppData%\Packages\<ชื่อแพ็กเกจ>\LocalCache\Local\...`) แต่แอปยังมองเห็นที่ path เดิม
- ตอนเปิดไฟล์ Windows หาในที่ส่วนตัวก่อน ถ้าไม่มีจึงไปเปิดไฟล์จริงใน AppData และ**แก้ไฟล์เดิมที่มีอยู่แล้วได้ตรงๆ** ไม่ถูกย้าย
- ตอนถอนการติดตั้ง Windows ลบเฉพาะไฟล์ที่อยู่ในที่ส่วนตัว
- ที่มา: https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes และ https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization (Microsoft เขียนว่าวิธีนี้ทำให้รุ่นที่เป็นแพ็กเกจ "pick up where an unpackaged version of the same app left off")

**ผลกับผู้ใช้ที่ย้ายจากรุ่น GitHub**
- ✅ ตรายาง Digital ID และการตั้งค่าเดิม รุ่น Store เห็นทันที ไม่ต้องเขียนโค้ดย้าย
- ⚠️ ของที่สร้าง**ใหม่**ในรุ่น Store (Digital ID ใหม่, ลายเซ็นรูปใหม่, ไฟล์ที่ยังไม่เคยมี) อยู่ในที่ส่วนตัวของแพ็กเกจ
  - รุ่น GitHub มองไม่เห็นของพวกนี้
  - ถ้าถอนรุ่น Store ของพวกนี้หาย **อันตรายสุดคือ Digital ID ใหม่**
- ⚠️ ถ้าโค้ดบันทึกไฟล์แบบ "เขียนลง temp แล้ว Move ทับ" ไฟล์ temp นับเป็นไฟล์ใหม่ ไฟล์ที่ได้อาจไปอยู่ในที่ส่วนตัวแทนไฟล์จริง: **ยังไม่ยืนยัน** ต้องทดสอบในเครื่องจริง
- ✅ **ผลทดสอบจริง 2026-11-11 (1.2.1 test MSIX, บัญชี Windows ทดสอบที่ไม่เคยมีข้อมูล YRK):** เปิด PDF แก้แล้ว Ctrl+S บันทึกทับได้และเปิดใหม่ถูก; `recent.json`, `reading-positions.json`, `stamps.json` (สร้างตรายางของฉัน) ไปอยู่ที่ `Packages\YRK.EasyPdf.Test_…\LocalCache\Local\YRK\EasyPdf` ทั้งหมด โฟลเดอร์จริง `AppData\Local\YRK\EasyPdf` ไม่ถูกสร้าง; ถอนแพ็กเกจแล้วไม่เหลือข้อมูลทั้งสองที่ (ยืนยันว่าถอนรุ่น Store = ข้อมูลที่สร้างในรุ่น Store หายด้วย); ยังไม่ได้ทดสอบ: บัญชีที่มีข้อมูลรุ่น GitHub อยู่ก่อน (อ่านทะลุ/แก้ไฟล์เดิม), Digital ID, การถอนรุ่น Velopack; สังเกต: หลังกด Save หน้า Home กระพริบแวบหนึ่ง (บันทึกได้ปกติ ยังไม่รู้ว่าเกิดเฉพาะ MSIX หรือไม่)
- ⚠️ ปุ่ม "เปิดโฟลเดอร์ข้อมูล" (`Views/PrivacyDialog.xaml.cs:53`) เปิด Explorer ซึ่งเห็นแค่โฟลเดอร์จริง ไม่เห็นไฟล์ในที่ส่วนตัว ผู้ใช้อาจคิดว่าข้อมูลหาย
- ถ้าเปิดทั้งสองรุ่นในเครื่องเดียว ไฟล์เดิมจะถูกแก้ร่วมกัน แต่ไฟล์ใหม่แยกกัน ข้อมูลจะเริ่มไม่ตรงกัน

**ทางปิด virtualization:** ใช้ `unvirtualizedResources` กับ `ExcludedDirectories` ของ `LocalAppData\YRK\EasyPdf` ได้ แต่ Microsoft เขียนว่า capability นี้ทำไว้สำหรับเกมบางประเภทและ "not intended to be used for other scenarios" ส่งเข้า Store **น่าจะไม่ผ่าน** (ที่มา: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations หน้าแก้ล่าสุด 2026-10-02)

**วิธีที่แนะนำ**
1. คง path เดิมในโค้ด ไม่ต้องย้ายไฟล์ ให้ Windows รวมมุมมองให้เอง
2. เพิ่ม "สำรองข้อมูล / นำเข้าข้อมูล" ทำเป็นไฟล์ zip เดียว ทำในทั้งสองรุ่น ใช้ได้ทั้งตอนย้ายเครื่องและก่อนถอนรุ่น Store
   - Digital ID ต้องให้ผู้ใช้ตั้งรหัสก่อนส่งออก ส่วนนี้ Claude ต้องตรวจความปลอดภัย
3. รุ่น Store ให้ปุ่ม "เปิดโฟลเดอร์ข้อมูล" ชี้ไปที่ตำแหน่งจริงในโฟลเดอร์ของแพ็กเกจ
4. แนะนำผู้ใช้ให้เลือกใช้**รุ่นเดียว** ใส่ไว้ในหน้าเว็บและ FAQ: "ย้ายไป Store: ติดตั้งรุ่น Store เปิดดูว่าข้อมูลครบ แล้วถอนรุ่น GitHub"
   - **ยังไม่ยืนยัน**ว่าการถอนรุ่น Velopack ลบเฉพาะโฟลเดอร์โปรแกรม (`%LocalAppData%\YRK.EasyPdf`) และไม่แตะ `YRK\EasyPdf` ต้องลองจริงก่อนเขียนคำแนะนำ
5. ทดสอบจริงก่อนปล่อย: ติดตั้งรุ่น GitHub สร้างข้อมูล ติดตั้งรุ่น Store แล้วลองแก้และสร้างของใหม่ จากนั้นถอนทีละรุ่น ดูว่าอะไรอยู่หรือหาย (ห้ามใช้ข้อมูลจริงของ Ken)

## 3. ลิงก์รับเงิน GitHub Sponsors และ QR พร้อมเพย์

ที่มา: Store Policies 7.20 ข้อ 10.8 https://learn.microsoft.com/en-us/windows/apps/publish/store-policies

- **10.8.1:** ข้อบังคับให้ใช้ระบบจ่ายเงินของ Microsoft ใช้กับเกมและ Xbox เท่านั้น แอปที่ไม่ใช่เกมบน PC ใช้ระบบจ่ายเงินอื่นได้
- **10.8.2:** การรับบริจาค "ต้องใช้ Microsoft payment request API หรือ secure third-party purchase API" และถ้าผู้บริจาคได้อะไรตอบแทน (ฟีเจอร์เพิ่ม, เอาโฆษณาออก) ต้องใช้ระบบของ Microsoft
  - YRK ไม่ให้อะไรตอบแทน จึงใช้ช่องทางภายนอกได้
  - ต้อง "note the use of a secure third-party purchase API in Partner Center" ตอนส่งแอป
  - GitHub Sponsors จ่ายในเว็บของ GitHub ส่วน QR พร้อมเพย์จ่ายในแอปธนาคาร YRK ไม่เก็บข้อมูลการเงินเลย
- **ยังไม่ยืนยัน:** ผู้ตรวจจะนับ QR พร้อมเพย์ที่เป็นแค่รูปภาพว่าเป็น "secure third-party purchase API" หรือไม่ กฎไม่ได้พูดถึง QR ตรงๆ
- **10.8.3 (เสี่ยง):** บัญชีบุคคล "cannot require financial information for primary functionality" กฎนับรวม "tax ID information" และ "private keys"
  - ฟีเจอร์หลักของ YRK คือแก้ PDF จึงน่าจะไม่เข้าข่าย แต่ e-Tax ใช้เลขผู้เสียภาษี และ Digital ID ใช้กุญแจส่วนตัว
  - **ยังไม่ยืนยัน**ว่าผู้ตรวจจะตีความอย่างไร ควรอธิบายไว้ใน certification notes ว่าเป็นฟีเจอร์เสริม ทำงานในเครื่อง และไม่ส่งข้อมูลออก

**ทางเลือกสำหรับรุ่น Store**
- (ก) ใช้หน้าต่างสนับสนุนเหมือนเดิม และแจ้งใน Partner Center เสี่ยงโดนส่งกลับให้แก้ แต่ไม่มีผลเสียอื่น
- (ข) รุ่น Store แสดงแค่ปุ่ม "เว็บไซต์ผู้พัฒนา" ส่วน Sponsors และ QR ย้ายไปไว้บนเว็บ ปลอดภัยที่สุด และตรงกับกฎกลางที่บันทึกไว้แล้วสำหรับทุก Store

## 4. WACK และการตรวจรับรอง

ที่มา: https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit-tests (หน้าแก้ล่าสุด 2026-11-05) และหน้าความสามารถ (capability) ด้านบน

| เรื่อง | ผลกับ YRK | ต้องทำ |
|---|---|---|
| **runFullTrust** | แอป WPF แบบ full trust "needs to declare the runFullTrust restricted capability" เป็นเรื่องปกติของแอป desktop ที่ลง Store | ใส่ใน manifest และอธิบายใน Partner Center ว่าเป็นแอป desktop WPF |
| **ไฟล์ native ของ PDFium, Tesseract** | WACK ตรวจว่าไฟล์ native เปิด DEP/ASLR และไม่มีส่วนที่เขียนและรันได้พร้อมกัน ไฟล์เหล่านี้เป็นของคนอื่น build | รัน WACK กับแพ็กเกจจริงแล้ว 2026-11-11: ผ่าน (ดูแถว "ผล WACK จริง") |
| **ผล WACK จริง (2026-11-11, 1.2.1 test MSIX)** | `build/make-test-msix.ps1` (YrkStore, self-signed) รันบนบัญชีทดสอบ: WACK ประเภท Centennial 24 ข้อ **OVERALL PASS** รวม Install signed executable files, Banned file analyzer, Platform appropriate files, Debug configuration, Special use capabilities, DPIAwareness; ข้อเดียวที่ FAIL คือ "Blocked executables" ซึ่งเป็นข้อ **optional** (มีการอ้างถึง Process.Start/CreateProcess/ShellExecute ใน YrkEasyPdf.dll, .NET/WPF runtime, PdfSharp, tesseract50.dll) ปกติของแอป desktop full trust ไม่บล็อกการส่ง; ชุด Centennial นี้ไม่มีข้อตรวจ DEP/ASLR แยก | ไม่ต้องแก้ ตอนส่ง Partner Center ถ้าถูกถามให้อธิบายว่าเปิดลิงก์เว็บ/โฟลเดอร์ด้วย ShellExecute |
| **x64 เท่านั้น** | ทำได้ แพ็กเกจ x64 ห้ามมีไฟล์ ARM ไฟล์ x86 ปนได้แต่ไม่ควรมี | ตรวจว่า publish ไม่มีโฟลเดอร์ `arm64` หรือ `x86` (`Yrk.PdfStudio.App.csproj:19` ลบ x86 ให้แล้ว ส่วน arm64 ยังไม่ได้เช็ก) |
| **Debug build** | ต้องเป็น Release เท่านั้น | ใช้คำสั่ง publish เดิมแบบ Release |
| **ห้ามมี .pfx/.snk ในแพ็กเกจ** | ต้องไม่มีไฟล์ทดสอบหรือกุญแจหลุดเข้าไป | ตรวจรายชื่อไฟล์ก่อนส่ง |
| **รูปไอคอนใน manifest** | ต้องมีครบทุกขนาด และห้ามใช้รูปตัวอย่างของ Visual Studio | ทำไอคอนชุด MSIX จากไอคอน YRK PDF |
| **นโยบายความเป็นส่วนตัว (10.5.1)** | แอป Win32 "must always have privacy policies" และต้องใส่ URL ใน Partner Center | ใช้หน้านโยบายบนเว็บที่มีอยู่ ตรวจว่าระบุการเชื่อมต่อครบ |
| **การใช้เน็ต** | กฎไม่ได้บังคับให้ประกาศแยก นอกจากเขียนในนโยบายความเป็นส่วนตัว แอป full trust ไม่ต้องขอ `internetClient` (**ยังไม่ยืนยัน** ต้องลองตอน build) | นโยบายต้องระบุ: ประทับเวลา DigiCert, CRL, ดาวน์โหลดภาษา OCR จาก GitHub, ฟอร์มความเห็น, ลิงก์เว็บ (รุ่น Store ไม่มี feed อัปเดต) |
| **10.2.2 ห้ามโหลดโค้ดมารัน** | ไฟล์ภาษา OCR เป็นข้อมูล ไม่ใช่โค้ด และตรวจ SHA-256 อยู่แล้ว น่าจะผ่าน | อธิบายใน certification notes |
| **10.2.7 ถอนได้สะอาด** | MSIX ทำให้เอง | – |

## 5. การเซ็นแพ็กเกจและ SmartScreen

ที่มา: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options (หน้าแก้ล่าสุด 2026-08-29)

- **ยืนยันแล้ว:** ส่งแบบ MSIX ทาง Store แล้ว "Microsoft re-signs the package after certification" ฟรี ไม่ต้องซื้อใบรับรอง และผู้ใช้ไม่เห็นคำเตือน SmartScreen
- ใช้ได้เฉพาะตัวที่ติดตั้งจาก Store ส่วน `.exe` บน GitHub ยังไม่มีลายเซ็นเหมือนเดิม และยังขึ้น SmartScreen
- ถ้าส่งเป็น `.exe/.msi` เข้า Store แทน (ข้อ 10.2.9) Microsoft **ไม่**เซ็นให้ ต้องซื้อใบรับรองเอง จึงไม่คุ้ม
- Azure Artifact Signing (ราคาประมาณ $9.99 ต่อเดือน) ให้บุคคลใช้ได้เฉพาะในสหรัฐฯ และแคนาดา Ken ใช้ไม่ได้
- ใบรับรอง OV ราคาประมาณ $150–300 ต่อปี และก็ยังต้องรอให้สะสม reputation ก่อนคำเตือนจะหายไป
- SignPath Foundation เซ็นให้โปรเจกต์โอเพนซอร์สฟรี ใช้ได้กับตัวติดตั้งบน GitHub ถ้าผ่านเกณฑ์ (**ยังไม่ยืนยัน**ว่า YRK ผ่าน)

## 6. ค่าใช้จ่ายและบัญชี

ที่มา: https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/opening-a-developer-account (หน้าแก้ล่าสุด 2026-10-29) และ Store Policies ข้อ 10.14

- **ค่าสมัคร: ฟรี** ทั้งบัญชีบุคคลและบริษัท ต้องเริ่มที่ https://storedeveloper.microsoft.com เท่านั้น ถ้าเข้าทางอื่นจะเจอขั้นตอนแบบเก่า
- **ยืนยันตัวตน (บุคคล):** บัตรที่ราชการออกตัวจริงกับเซลฟี ถ่ายจากมือถือ และต้องใช้ Microsoft account ส่วนตัว
- **เปลี่ยนจากบัญชีบุคคลเป็นบริษัททีหลังไม่ได้** ต้องเปิดบัญชีใหม่
- **ยังไม่ยืนยัน:** หน้านี้ไม่ได้ระบุรายชื่อประเทศ จึงยังไม่เห็นว่าเขียนถึงไทยตรงๆ แต่เขียนว่าการเซ็นของ Store ใช้ได้ "Worldwide"
- **10.14 (ต้องตัดสินใจ):** ต้องใช้บัญชีบริษัทถ้า "a reasonable consumer would interpret your application or publisher name to be that of a business entity" หรือแจกแอปเกี่ยวกับอาชีพการงาน
  - ชื่อ "YRK LAB" อาจดูเหมือนบริษัท
  - บัญชีบริษัทต้องมีเลข DUNS หรือเอกสารจดทะเบียน และอีเมลโดเมนบริษัท (ใช้ Gmail ไม่ได้)
  - ถ้าใช้บัญชีบุคคล ผู้จัดพิมพ์จะเป็นชื่อจริงของ Ken ซึ่งแสดงในหน้า Store **ยังไม่ยืนยัน**ว่าใช้ชื่อแสดงอื่นได้หรือไม่

---

## ทางที่แนะนำ (ทำทีละขั้น)

1. **ใน 1.x (หน้าตาไม่เปลี่ยน):**
   - เพิ่มตัวตรวจ "รันจาก MSIX หรือไม่" (`GetCurrentPackageFullName`) และ build flag `YrkStore` ที่ปิด Velopack
   - เพิ่มเทสต์ว่าในโหมด Store ไม่สร้างตัวอัปเดต
2. **ใน 1.x:** ทำ "สำรอง / นำเข้าข้อมูล" (zip, ให้ตั้งรหัสสำหรับ Digital ID) ผู้ใช้รุ่น GitHub ได้ประโยชน์ด้วย
3. **Ken เปิดบัญชี Partner Center** หลังตัดสินใจเรื่องบัญชีบุคคลหรือบริษัท แล้วจองชื่อแอปไว้ก่อน (การจองชื่อไม่ต้องส่งแอป)
4. **ทำแพ็กเกจ MSIX ทดลอง** (x64, runFullTrust, ไอคอนครบ) แล้วรัน WACK ในเครื่อง แก้สิ่งที่ไม่ผ่าน โดยเฉพาะไฟล์ native
5. **ทดสอบการย้ายข้อมูลจริง** ตามข้อ 2 ข้อ 5 ด้วยข้อมูลทดสอบ และแก้ปุ่ม "เปิดโฟลเดอร์ข้อมูล" ของรุ่น Store
6. **ปรับนโยบายความเป็นส่วนตัว**ให้ระบุการเชื่อมต่อของรุ่น Store และหน้าสนับสนุนตามทางเลือกในข้อ 3
7. **2.0.0:** ส่งรุ่น Store พร้อม certification notes (full trust, ช่องทางบริจาคภายนอก, e-Tax และ Digital ID ทำงานในเครื่อง) รุ่น GitHub ปล่อยด้วย Velopack คู่กันเหมือนเดิม
8. หลังผ่าน: เพิ่มปุ่ม "ดาวน์โหลดจาก Microsoft Store" บนหน้าเว็บ พร้อมคำแนะนำการย้ายจากรุ่น GitHub

## เรื่องที่ Ken ต้องตัดสินใจ

1. **บัญชีบุคคลหรือบริษัท:** ชื่อ "YRK LAB" กับข้อ 10.14, ชื่อจริงที่จะแสดงในหน้า Store และ e-Tax/Digital ID กับข้อ 10.8.3
2. **หน้าสนับสนุนในรุ่น Store:** (ก) คงไว้ทั้ง Sponsors และ QR แล้วแจ้งใน Partner Center หรือ (ข) เหลือแค่ลิงก์เว็บไซต์
3. **ผู้ใช้ย้ายรุ่นหรือไม่:** แนะนำผู้ใช้เดิมให้ย้ายไป Store หรือปล่อยให้เลือกเอง (ผู้ใช้รุ่น GitHub อัปเดตต่อได้ตลอด)
4. **รุ่น Store ขายหรือฟรี:** แนะนำให้ฟรีเหมือนเดิม
5. **ภาษาในหน้า Store:** ไทยและอังกฤษ ตามข้อ 10.7 ต้องแปลคำอธิบายครบทุกภาษาที่ประกาศ
6. **ตัวติดตั้ง GitHub:** จะลองขอ SignPath Foundation เพื่อลด SmartScreen ด้วยหรือไม่ (ไม่เกี่ยวกับ Store)

## แหล่งที่มา (ตรวจ 2026-11-06)

- Microsoft Store Policies 7.20: https://learn.microsoft.com/en-us/windows/apps/publish/store-policies
- การทำงานของแอป desktop ที่เป็นแพ็กเกจ (AppData, ไฟล์อ่านอย่างเดียว, การถอน): https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes
- Flexible virtualization: https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization
- App capability declarations (runFullTrust, unvirtualizedResources): https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations
- WACK tests: https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit-tests
- Code signing options: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options
- เปิดบัญชีนักพัฒนา: https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/opening-a-developer-account
- GetCurrentPackageFullName: https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getcurrentpackagefullname
- Velopack docs (ไม่พบเรื่อง MSIX): https://docs.velopack.io/
