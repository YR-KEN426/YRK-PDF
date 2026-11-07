# แผน 1.2.0: ใบเพิ่มหนี้/ใบลดหนี้ + ส่วนลด, จำหน้าที่อ่านค้าง, ใบรับรอง (RSA-PSS + 2 จุดเล็ก), ป้ายบนสิ่งที่ YRK วาง

## ที่มา
ลำดับรุ่นที่ Ken ตกลง (PROJECT_NOTES.md): 1.2.0 = ใบเพิ่มหนี้/ลดหนี้ + ส่วนลด + จำหน้าที่อ่านค้าง + 2 จุดเล็กของใบรับรอง + RSA-PSS + ป้ายบนสิ่งที่ YRK วาง และเก็บงานค้างจาก 1.1.9 (release.ps1 ไม่ build คู่มือ, เทสต์เขียน log ลงเครื่องจริง)

Ken ตัดสินใจ (2026-11-07):
- เลขที่เอกสารแยกต่อประเภท (ใบกำกับ / ใบลดหนี้ / ใบเพิ่มหนี้ จำเลขล่าสุดแยกกัน)
- ส่วนลดพิมพ์เป็นบาทหรือ % ได้ ทั้งรายบรรทัดและท้ายบิล (XML เก็บเป็นจำนวนเงิน)
- ใบเพิ่ม/ลดหนี้: กรอกเลขที่ วันที่ มูลค่าเดิมของใบกำกับเดิม และเหตุผลเอง
- จำหน้าที่อ่านค้าง: เปิดตลอด ไม่มีปุ่มปิด

เมื่ออนุมัติ: เขียนแผนนี้ลง `plan.md` แทนแผน 1.1.9 (แผนเก่าอยู่ใน git) บันทึกการตัดสินใจลง `PROJECT_NOTES.md` แล้ว commit

## สิ่งที่ผู้ใช้จะเห็น

### e-Tax
- ช่อง "ส่วนลด" ในตารางรายการ และช่อง "ส่วนลดท้ายบิล" ใต้ตาราง พิมพ์ `50` = 50 บาท, `10%` = 10%
- เลือกประเภท "ใบเพิ่มหนี้ (80)" / "ใบลดหนี้ (81)" แล้วมีกล่องเพิ่ม: เลขที่ใบกำกับเดิม, วันที่, มูลค่าเดิม, เหตุผล (รายการตามรหัสของสเปก + พิมพ์เพิ่มได้)
- PDF ที่ได้แสดงส่วนลด, ยอดหลังลด และสำหรับใบเพิ่ม/ลดหนี้แสดง มูลค่าเดิม / มูลค่าที่ถูกต้อง / ผลต่าง / VAT ของผลต่าง ตามหลักใบลดหนี้ (ม.86/10)
- เลขที่เอกสารนับแยกต่อประเภท

### อื่นๆ
- เปิดไฟล์เดิมอีกครั้งแล้วกลับไปหน้าที่อ่านค้างเอง (จำ 200 ไฟล์ล่าสุด เก็บในเครื่องเท่านั้น ไม่เขียนลง PDF)
- ลายเซ็นที่ใบรับรอง self-signed ใช้ RSA-PSS กดเชื่อถือได้
- ลายนิ้วมือ SHA-256 แสดงเป็นกลุ่มละ 4 ตัว (หน้าต่างยืนยัน, รายการ, ข้อมูล Digital ID)
- หน้าต่าง "ใบรับรองที่ฉันเชื่อถือ" ตอนว่างมีคำอธิบายแทนกรอบขาว
- ป้ายในไฟล์บนรูป ตรายาง ลายเซ็นที่ YRK วาง: มองไม่เห็น หน้าตาไฟล์ไม่เปลี่ยน (เตรียมให้ 1.2.1 แก้รูปที่บันทึกแล้วได้)

## ขั้นตอน

### ขั้น 1: อ่านสเปก ETDA ของ 80/81 และส่วนลด (Claude ทำเอง: ตีความสเปก)
- แหล่ง: ETDA ขมธอ. 3-2560 v2.0 (PDF ภาษาอังกฤษจาก etda.or.th, ฉบับไทยเป็นฉบับหลัก) และ `XMLSchemaV2.zip` จาก etax.rd.go.th (ถาม Ken ก่อนดาวน์โหลด; เก็บใน scratchpad ไม่เข้า repo) อ่านข้อความ PDF ด้วยโปรแกรมเล็กที่อ้าง `Yrk.PdfStudio.Core.dll` ตามบทเรียน 1.1.7
- สิ่งที่ต้องได้: `docs/etax-debit-credit-notes.md` สรุป TypeCode 80/81, `ExchangedDocument/Purpose` + `PurposeCode` (รายการรหัสเหตุผล), `ApplicableHeaderTradeAgreement/AdditionalReferencedDocument` หรือ `InvoiceReferencedDocument` (ตามที่ XSD กำหนด), ช่องยอดใน `SpecifiedTradeSettlementHeaderMonetarySummation` (OriginalInformationAmount, LineTotalAmount, DifferenceInformationAmount, AllowanceTotalAmount, TaxBasisTotalAmount ฯลฯ), โครง `SpecifiedTradeAllowanceCharge` ระดับบรรทัดและหัวบิล, XMP `DocumentType` ของแต่ละประเภท; ทุกข้อขัดกันให้ยึด XSD (กฎใน PROJECT_NOTES)
- จากนั้นเติมรายละเอียดชื่อ element ลงขั้น 2–3 ใน `plan.md` ให้ Ken ดูก่อนส่ง Codex

### ขั้น 2: Core ส่วนลด (Codex `gpt-6.1-sol` · high)
- ไฟล์: `src/Yrk.PdfStudio.Core/Pdf/ETax/TaxInvoice.cs`, `tests/Yrk.PdfStudio.Core.Tests/ETaxTests.cs`
- `TaxInvoiceLine` เพิ่มส่วนลด (`Discount` = ค่า + เป็น % หรือไม่) และ `TaxInvoice` เพิ่มส่วนลดท้ายบิล; `Amount` = ยอดหลังลดของบรรทัด ปัด 2 ตำแหน่ง AwayFromZero เหมือนเดิม
- ยอดรวม: LineTotal → ส่วนลดท้ายบิล → TaxBasis → VAT (ทั้งแบบราคารวม/ไม่รวม VAT) → GrandTotal; `LineVats()` กระจายส่วนลดท้ายบิลให้ VAT รายบรรทัดรวมได้เท่ายอด VAT
- XML: AllowanceCharge ระดับบรรทัด/หัวบิล และ AllowanceTotalAmount ตามขั้น 1
- `Problems()`: ส่วนลดติดลบ, ส่วนลดเกินยอดบรรทัด/เกินยอดรวม, % เกิน 100
- **ไม่มีส่วนลด → XML ทุก byte เหมือนเดิม** (เทสต์ golden เดิม :195-218 ต้องผ่าน)
- เทสต์: ลดบาท/ลด % รายบรรทัด, ลดท้ายบิล, ราคารวม VAT + ส่วนลด, VAT รายบรรทัดรวมเท่ายอด, ปัดเศษสตางค์ขอบ, ข้อผิดพลาดแต่ละแบบ

### ขั้น 3: Core ใบเพิ่มหนี้/ใบลดหนี้ (Codex `gpt-6.1-sol` · high)
- ไฟล์: `TaxInvoice.cs`, `ETaxTests.cs`
- `TaxDocumentType` เพิ่ม `DebitNote` (80), `CreditNote` (81); `TypeCode`, `ThaiName`, `XmpExtension` (DocumentType ตามประเภท ตอนนี้ฝัง "Tax Invoice" ไว้ :151-159)
- `TaxInvoice` เพิ่ม `Reference` (เลขที่, วันที่, มูลค่าเดิม) และ `Reason` (รหัส + ข้อความ) ใช้เฉพาะ 80/81
- ยอด: รายการในใบคือ "ผลต่าง"; มูลค่าที่ถูกต้อง = มูลค่าเดิม ± ผลต่าง; VAT คิดจากผลต่าง
- `Problems()`: 80/81 ต้องมีเลขที่/วันที่/มูลค่าเดิม/เหตุผล; ใบลดหนี้ลดเกินมูลค่าเดิมไม่ได้; ประเภทอื่นห้ามมี Reference
- เทสต์: XML ของ 80/81 มี element ตามขั้น 1 ครบและเรียงตาม XSD, ยอดผลต่างถูก, XMP DocumentType, PDF/A-3 แนบ XML ได้, 388/T02–T04 เหมือนเดิม

### ขั้น 4: App ฟอร์มและหน้า PDF (Codex `gpt-6.1-sol` · medium)
- ไฟล์: `Views/ETaxInvoiceDialog.xaml(.cs)`, `Imaging/TaxInvoiceRenderer.cs`, `Services/ETaxSettings.cs`, `ViewModels/MainViewModel.ETax.cs`, `Localization/th.json` + ไฟล์อังกฤษ, เทสต์ใน `ETaxTests.cs` (ส่วนที่เรียก `Build()` :346-457)
- ฟอร์ม: คอลัมน์ส่วนลดใน `ItemsGrid` (`ItemRow` :178-219 รับ `50` หรือ `10%`), ช่องส่วนลดท้ายบิล, กล่องอ้างใบเดิม + เหตุผล แสดงเฉพาะ 80/81, `Types` (:16-22) เพิ่ม 2 ประเภท, `Update()` แสดงยอดหลังลด
- หน้า PDF: คอลัมน์ส่วนลด (แสดงเมื่อมีส่วนลดอย่างน้อยบรรทัดเดียว), แถวยอดเพิ่ม (รวม/ส่วนลด/หลังลด/VAT/สุทธิ; 80/81: มูลค่าเดิม/ที่ถูกต้อง/ผลต่าง/VAT/รวม), กล่องอ้างใบเดิมและเหตุผล, ขยาย `FooterTop` ให้พอ (ตอนนี้ 150pt)
- ตั้งค่า: `LastNumber` แยกต่อประเภท ย้ายค่าเดิมเข้าประเภทที่เคยเลือกโดยไม่หาย; `etax.json` เก่าเปิดได้
- เทสต์: `Build()` กับส่วนลดบาท/% และ 80/81, ไฟล์ตั้งค่าเก่าย้ายเลขถูก, เลขรันแยกประเภท; ตรวจคำแปลด้วย `build\check-translations.ps1`

### ขั้น 5: ใบรับรอง RSA-PSS + 2 จุดเล็ก (Codex `gpt-6.1-sol` · high, ความปลอดภัย Claude ตรวจละเอียด)
- ไฟล์: `src/Yrk.PdfStudio.Core/Pdf/Signatures/PdfSignatureVerifier.cs` (`IsSelfSigned` :1485-1524), `App/ViewModels/SignatureInfoViewModel.cs` (:101-103), `App/Services/TrustedCertificateStore.cs`, `Views/TrustCertificateDialog.xaml.cs`, `Views/TrustedCertificatesDialog.xaml(.cs)`, `Views/DigitalSignDialog.xaml.cs`, `th.json` + อังกฤษ, เทสต์ `SignatureUserTrustTests.cs`, `TrustedCertificateStoreTests.cs`
- `IsSelfSigned` รองรับ `1.2.840.113549.1.1.10`: อ่าน RSASSA-PSS-params; รับเฉพาะ hash SHA-256/384/512 ที่ MGF1 ใช้ hash เดียวกัน, salt = ความยาว hash, trailer = 1; ไม่มี params (ค่าเริ่มต้น SHA-1) → ปฏิเสธ; ตรวจด้วย `RSASignaturePadding.Pss`; สาขา PKCS#1/ECDSA เดิมไม่แตะ
- เปิด `IsSelfSigned` เป็น public helper ใน Core ให้ `CanTrustCertificate` ใช้ตัวเดียวกัน (ตอนนี้เทียบแค่ชื่อ Subject = Issuer ทำให้ปุ่มขึ้นแต่กดแล้วไม่มีผล)
- ลายนิ้วมือ: เพิ่ม `TrustedCertificateStore.DisplayFingerprint` จัดกลุ่มละ 4 ตัว (แบบ `UpdateSignature.cs:133-139`) ใช้ใน `Describe()` เท่านั้น; `Fingerprint` (ชื่อไฟล์) คงเดิม
- รายการว่าง: ข้อความอธิบายว่าเพิ่มได้จากปุ่ม "เชื่อถือใบรับรองนี้…" ในแผงลายเซ็น หรือ "นำเข้า .cer"
- เทสต์: PSS self-signed → TrustedByUser; PSS ลายเซ็นเสีย → ไม่ผ่าน; PSS ออกโดย CA ชื่อเดียวกัน → ไม่ผ่าน; params ผิด (salt อื่น, MGF hash อื่น) → ไม่ผ่าน; ปุ่มไม่ขึ้นกับใบที่ชื่อเหมือนแต่ไม่ได้ self-signed; แก้เทสต์ :34 ที่หา fingerprint แบบไม่มีช่องว่าง
- mutation check: ลบการตรวจ salt/MGF แล้วเทสต์ต้อง fail

### ขั้น 6: ป้ายบนสิ่งที่ YRK วาง (Codex `gpt-6.1-sol` · high)
- ไฟล์: `Core/Pdf/Pdfium/PdfiumNative.cs`, `PdfiumDocument.Editing.cs` (`AddImage` :343, `AddVector` :460, `AddPdfPage` :519), `IPdfEngine.cs` (`PlacementStyle` :7 หรือพารามิเตอร์ใหม่), `Core/Pdf/Text/PdfTextWriter.cs` (:85-104), `App/ViewModels/MainViewModel.Editing.cs` (`CommitOverlaysAsync` :696-755), `MainViewModel.SignatureArt.cs`, `MainViewModel.PlacedText.cs`, เทสต์ `PdfEditingTests.cs`
- เพิ่ม P/Invoke ของ PDFium 8100: `FPDFPageObj_AddMark`, `FPDFPageObjMark_SetStringParam`, และตัวอ่าน (`CountMarks`, `GetMark`, `GetName`, `GetParamStringValue`) สำหรับเทสต์และ 1.2.1
- ป้ายชื่อ `YRK` พร้อม `Kind` (Image / Stamp / StampText / Signature / Text), `Group` (GUID ต่อชิ้นที่วาง), `V` = 1
  - รูป/form object: ใส่ป้ายก่อน `FPDFPage_InsertObject`; vector: ทุก path ของชิ้นเดียวกันใช้ Group เดียวกัน
  - คำจริง (ตรายาง/ข้อความที่วาง): `/YRK <</Kind/StampText/Group(...)/V 1>> BDC … EMC` ใน `PdfTextWriter` ซ้อนกับ `/OC` เดิมให้ถูกลำดับ
  - กรอบตรายางกับคำของตรายางใช้ Group เดียวกัน
- ใส่ป้ายเฉพาะชิ้นที่ผู้ใช้วางใน App (ไม่ใส่ใน Batch/Recovery/e-Tax รอบนี้)
- เทสต์: บันทึกแล้วอ่านป้ายกลับได้ทุกชนิด, ตรายาง Group ตรงกัน, render หน้าก่อน/หลังใส่ป้ายเหมือนกันทุกพิกเซล, ไฟล์ที่มีลายเซ็นแล้ววางรูปเพิ่ม ลายเซ็นเดิมยังถูกต้อง, หน้าที่มี /Rotate

### ขั้น 7: จำหน้าที่อ่านค้าง (Codex `gpt-6.1-sol` · medium)
- ไฟล์: `App/Services/ReadingPositions.cs` (ใหม่, แบบ `RecentFiles.cs`), `ViewModels/MainViewModel.cs` (`OpenFileAsync` :287, `LoadDocument` :476, `ReleaseDocumentAsync` :522), ที่ปิดแท็บ/ปิดโปรแกรม, เทสต์ใน `tests/.../App/`
- เก็บ `%LOCALAPPDATA%\YRK\EasyPdf\reading-positions.json`: path ที่ normalize (ไม่สนตัวพิมพ์) → เลขหน้า + เวลา, สูงสุด 200 รายการ, ข้ามไฟล์ใน temp เหมือน `RecentFiles`
- บันทึกตอนปิดเอกสาร/แท็บ/โปรแกรม; เปิดไฟล์ใหม่ (ไม่ใช่ reopen ที่มี `restore`) แล้ว `GoToPage` ถ้าหน้ายังมีอยู่ ไม่อย่างนั้นหน้า 1; ไม่กลับหน้าเมื่อหน้าที่จำคือหน้า 1
- ล้างรายการไฟล์ล่าสุดแล้วล้างตำแหน่งด้วย (ถ้ามีคำสั่งล้าง)
- เทสต์: บันทึก/โหลด, เกิน 200 ตัดรายการเก่า, ไฟล์ json เสียไม่แครช, หน้าที่จำเกินจำนวนหน้า → หน้า 1, ใน `AppTestHarness` เปิด-ไปหน้า 5-ปิด-เปิดใหม่ → หน้า 5; เทสต์ใช้โฟลเดอร์ temp ไม่แตะไฟล์จริง

### ขั้น 8: งานค้างจาก 1.1.9 (Claude แก้เอง ≤30 บรรทัด/ไฟล์ + Codex `gpt-6-luna` · low)
- `build/release.ps1`: รัน `build\build-guide.ps1` หลังเทสต์ (:145) ก่อน "== Publish" (:147) — Claude แก้เอง (สคริปต์ปล่อยรุ่น)
- log ของเทสต์: ให้ `App.LogFolder` (`App.xaml.cs:99-100`) เปลี่ยนได้จากเทสต์ (internal setter) แล้วตั้งในชุดเทสต์ไปโฟลเดอร์ temp ส่วนตัว (`PrivateTempDirectory.cs`) — luna

### ขั้น 9: เลขรุ่น, release notes, คู่มือ (Codex `gpt-6-luna` · low)
- เลขรุ่น 1.2.0 ใน `Directory.Build.props`, `docs/release-notes/1.2.0.md` (ไทย/อังกฤษ), `docs/guide/th.html` + `en.html` ส่วน e-Tax และการอ่าน
- เทียบชื่อปุ่มกับ `th.json` ทุกจุด (บทเรียน 1.1.9), ใช้คำว่า "ความทึบ"

### ขั้น 10: ตรวจ (Claude + Codex read-only + Ken)
- Claude ตรวจความปลอดภัยขั้น 5 ละเอียด และขั้น 6 ส่วนไฟล์ที่มีลายเซ็น
- `codex exec --sandbox read-only "ตรวจ git diff ของ 1.2.0 หาบั๊ก ช่องโหว่ และสิ่งที่ผิดจาก plan.md"`
- เปิดตัว Release ดูด้วยตา (บอก Ken ก่อน, สำรอง/คืน `*.json` ใน `%LOCALAPPDATA%\YRK\EasyPdf`) เพราะแก้ XAML หน้าต่าง e-Tax และหน้าต่างใบรับรอง

ส่ง Codex เป็นชุด: ขั้น 1 (Claude) → 2–3 → ตรวจ → 4 → ตรวจ → 5–6 → ตรวจ → 7–8 → ตรวจ → 9 → 10

## การตรวจ
- `dotnet test` ทั้งชุดผ่านทุกขั้น (build ไป `-p:OutDir=` ถ้า Ken เปิดโปรแกรมอยู่)
- Claude ดู `git diff` ทุกขั้น ส่วนความปลอดภัยอ่านละเอียด
- Ken ทดสอบเอง:
  - ส่ง XML ตัวอย่าง 4 แบบเข้า validateschema ของกรมสรรพากร (Schema + Schematron): ใบกำกับมีส่วนลดรายบรรทัด+ท้ายบิล, ราคารวม VAT + ส่วนลด, ใบลดหนี้ (81), ใบเพิ่มหนี้ (80) — Claude ใส่ไฟล์ให้ Ken ติ๊ก reCAPTCHA
  - ดู PDF ใบลดหนี้/เพิ่มหนี้ว่าอ่านง่ายและครบตามที่ต้องมี
  - เปิด e-book อ่านถึงหน้า 30 ปิด เปิดใหม่ → หน้า 30 (ทั้งเปิดจากไฟล์ล่าสุดและดับเบิลคลิกไฟล์)
  - วางรูป ตรายาง ลายเซ็น บันทึก แล้วเปิดใน Acrobat หน้าตาเหมือนเดิม
  - (ถ้ามี) ใบรับรอง RSA-PSS self-signed: เชื่อถือได้ และเทียบลายนิ้วมือแบบกลุ่มละ 4 ตัว
  - อัปเดต 1.1.9 → 1.2.0 จริง จากหน้า Home และแท็บเอกสาร

## สถานะ
- [ ] ขั้น 1: อ่านสเปก ETDA 80/81 + ส่วนลด
- [ ] ขั้น 2: Core ส่วนลด
- [ ] ขั้น 3: Core ใบเพิ่มหนี้/ใบลดหนี้
- [ ] ขั้น 4: App ฟอร์มและหน้า PDF
- [ ] ขั้น 5: RSA-PSS + ลายนิ้วมือ + รายการว่าง
- [ ] ขั้น 6: ป้ายบนสิ่งที่ YRK วาง
- [ ] ขั้น 7: จำหน้าที่อ่านค้าง
- [ ] ขั้น 8: release.ps1 build คู่มือ + log ของเทสต์
- [ ] ขั้น 9: เลขรุ่น, release notes, คู่มือ
- [ ] ขั้น 10: ตรวจ
