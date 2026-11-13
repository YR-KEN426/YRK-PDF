# แผน: รุ่น 1.2.2 สแกนเข้า PDF + งานเก็บ

## ที่มา
ลำดับรุ่นที่ Ken ตกลงไว้เมื่อ 2026-11-12 (PROJECT_NOTES "ลำดับรุ่นใหม่") กำหนดให้ 1.2.2 มีงานเหล่านี้
- สแกนเข้า PDF แบบเต็ม โดย Ken ยืนยันว่าไม่ลดขอบเขต
- เทสต์ App ส่วน OCR
- log ของเทสต์ไปอยู่ใน temp
- คำสั่ง `verify` ใน UpdateSigner และแก้ checklist
- เปลี่ยนลิงก์ในโปรแกรมเป็น `pdf.yrklab.com`
- merge PR ของ Dependabot ตอนต้นรุ่น

**ของที่มีอยู่แล้วและจะใช้ต่อ**
- OCR Tesseract: `PdfOcr.RecognizeAsync` + `AddTextLayer`, `OcrLanguageData` (ดาวน์โหลดภาษาไทย), `MainViewModel.Ocr.EnsureLanguageDataAsync`
- สร้าง PDF จากรูป: `IPdfEngine.CreateFromImagesAsync` (JPEG ผ่านตรง, BGRA เป็น Flate)
- ตรวจหน้าสแกน: `Pdf/Scan/ScanCleanup.Check` (หน้าว่าง มุมเอียง ขอบดำ), `App/Imaging/ImageConversion`, `WpfImageCodec.EncodeJpeg`
- รายการรูปพร้อมภาพย่อ: `Views/FileListDialog` (โหมด ImagesToPdf)
- เทสต์ App: `AppTestHarness` + `ScriptedDialogs`

**ยังไม่มี:** โค้ด WIA/TWAIN และตัวบีบอัดรูป 1 บิต

## สิ่งที่ Claude ตัดสินใจแทน (Ken ข้ามคำถาม ปรับได้ตอนอนุมัติ)
1. **ใช้ WIA 2.0 Automation** (`WIA.DeviceManager` ผ่าน COM แบบ late-bound)
   - ไม่ต้องมี interop dll และใช้ได้ทั้ง x64 และ x86
   - รันบนเธรด STA แยก
   - ใช้หน้าต่างตั้งค่าของ YRK เอง ไม่เรียกหน้าต่างของไดรเวอร์
2. **ปลายทางคือสร้างไฟล์ใหม่อย่างเดียว** การแทรกหน้าสแกนต่อท้ายไฟล์ที่เปิดอยู่ไว้รุ่นหลัง
   - ปุ่มอยู่ที่หน้า Home และแถบเครื่องมือ
3. **ตัดขอบได้ทั้งแบบอัตโนมัติและแบบลากกรอบเอง**
   - อัตโนมัติใช้ `ScanCleanup` จัดหน้าให้ตรง ตัดขอบดำ และติ๊กหน้าว่างไว้ให้ลบ
   - ผู้ใช้ลากกรอบแก้เองได้ทีละหน้า
4. **วิธีเก็บรูปในไฟล์**
   - สี/เทา: JPEG คุณภาพ 80 (ปรับได้)
   - ขาวดำ: รูป 1 บิตแบบ CCITT G4 ผ่าน PDFsharp 6.2.4 ที่มีอยู่แล้ว ถ้าทำไม่ได้ใช้ Flate 1 บิตแทน
   - ขนาดเป้าหมายคือไม่เกิน 100 KB ต่อหน้า A4 ขาวดำที่ 300 dpi
5. **ทำงานในเครื่องเท่านั้น**
   - รูปที่สแกนพักใน temp ส่วนตัวของโปรแกรม และลบเมื่อปิดหน้าต่าง ทั้งตอนปิดปกติ ตอนยกเลิก และตอนเกิด error
   - ไม่ส่งรูปออกนอกเครื่อง ยกเว้นการดาวน์โหลดภาษา OCR ที่มีอยู่เดิม
6. **log ของเทสต์:** เดิมแก้ไว้แล้วใน a35cda6 (`PrivateTempDirectory` ตั้ง `App.LogFolder`) แต่ log จริงยังมีรายการจาก `UiDriver` ปนอยู่
   - เพิ่มตัวแปร `YRK_EASYPDF_LOG_DIR` ให้ `UiDriver` และเครื่องมือทดสอบใช้

## ขั้นตอน

### ขั้น 0: merge PR ของ Dependabot (Claude ในแชตหลัก, Ken ยืนยันก่อน merge)
- รายการ PR
  - #1 กลุ่ม minor/patch
  - #2 PDFium 159 (Claude ตรวจ diff ก่อน)
  - #3 Test SDK 18
  - #5 xunit runner 4
  - #4 Pkcs 11 ไม่รับ ให้ปิด PR พร้อมเหตุผล
- ขั้นตอน: `gh pr list --author app/dependabot` → Ken ยืนยัน → merge ทีละ PR → `git pull`
- ทดสอบ: `dotnet test` ทั้งชุดผ่าน เปิด PDF ที่มีลายเซ็น เปิด DWG จริง และรัน OCR หนึ่งหน้า

### ขั้น 1: `verify` ใน UpdateSigner + checklist (Codex `gpt-6.1-sol` · medium)
- `build/UpdateSigner/Program.cs`: คำสั่ง `verify`
  - รับ `--package --id --version --sig [--content-sig] [--public]` โดย `--public` ใช้ `src/Yrk.PdfStudio.App/Assets/update-public-key.pem` เป็นค่าเริ่มต้น
  - เรียก `UpdateSignature.Verify` / `VerifyContent` แล้วพิมพ์ Fingerprint
  - ผ่านได้ exit 0 ไม่ผ่านได้ exit 1 และไม่ถามรหัสผ่าน
- `docs/release-checklist.md` (`gpt-6-luna` · low ส่งรวมรอบเดียวกัน)
  - เลขรุ่นเป็นตัวแปร `$Ver` / `$PrevVer`
  - ตัดขั้นสำรองและคืน feed (L5, L42)
  - ใส่ `cd "D:\Software Project\YRK-PDF"` ก่อนคำสั่งที่ใช้ path แบบ relative
  - เพิ่มขั้นรัน `verify` กับแพ็กเกจที่ปล่อยแล้ว
- ทดสอบ: เทสต์ใหม่ (ลายเซ็นถูกได้ 0, แก้ไฟล์ 1 ไบต์ได้ 1, กุญแจผิดได้ 1) และรัน verify กับแพ็กเกจ 1.2.1 จริงใน `artifacts\releases` ต้องผ่าน
- **Claude ตรวจเอง** เพราะเป็นส่วนการเซ็นอัปเดต

### ขั้น 2: ลิงก์โดเมนใหม่ + log (Codex `gpt-6-luna` · low)
- `src/Yrk.PdfStudio.App/Services/Feedback.cs` L33 → `https://pdf.yrklab.com/`
- `docs/guide/th.html` L18, 341, 404 และ `docs/guide/en.html` L18, 337, 399 (คง `#privacy` และ `en/`)
- `src/Yrk.PdfStudio.App/App.xaml.cs` `LogFolder`: ถ้ามี `YRK_EASYPDF_LOG_DIR` ให้ใช้ค่านั้น และให้ `tools/UiDriver` ตั้งค่านี้ไปที่ temp
- ทดสอบ
  - ค้น `github.io` ต้องเหลือแค่ `PROJECT_NOTES.md` และ `plan.md`
  - เทสต์ `LogFolder` อ่านตัวแปรได้
  - ทั้งชุดผ่าน
- (ยังไม่ build คู่มือ PDF รอขั้น 8 ทีเดียว)

### ขั้น 3: แกนสแกน: โมเดล, ตัวจำลอง, ประมวลผลรูป, สร้าง PDF (Codex `gpt-6.1-sol` · high)
ไฟล์ใหม่ใน `src/Yrk.PdfStudio.Core/Pdf/Scan/` และ `src/Yrk.PdfStudio.App/Scanning/`
- `IScannerService`
  - `ListDevicesAsync`
  - `GetCapabilitiesAsync(device)` คืนแหล่งกระดาษ (กระจก / ถาดป้อน / สองหน้า), dpi ที่รองรับ และโหมดสี
  - `ScanAsync(device, ScanSettings, IProgress<ScannedPage>, ct)` คืนหน้าทีละแผ่น
  - error ที่แยกชนิดได้: ไม่พบเครื่อง, เครื่องไม่ว่าง, กระดาษติด, ถาดว่าง, ยกเลิก
- `FakeScannerService` (ตัวจำลอง)
  - สร้างหน้าทดสอบที่มีข้อความไทย/อังกฤษ เอียงเล็กน้อย มีขอบดำ และมีหน้าว่าง
  - จำลอง error ได้ทุกชนิด
  - ใช้ทั้งในเทสต์และโหมด debug
- `ScanSession`: รายการหน้าที่เก็บ rotation, crop rect และสถานะ "หน้าว่าง" แก้ลำดับได้
  - เก็บไฟล์รูปใน temp ส่วนตัวของโปรแกรม `Dispose` ลบทั้งโฟลเดอร์
- `ScanPageProcessor`: หมุน → จัดหน้าตรง → ตัด → แปลงเป็นเทาหรือขาวดำ (threshold แบบ Otsu) → เข้ารหัส
- `ScanPdfBuilder`
  - สี/เทาใช้ JPEG ผ่าน `CreateFromImagesAsync`
  - ขาวดำใช้รูป 1 บิต CCITT G4 ผ่าน PDFsharp ถ้าไม่ได้ให้ใช้ Flate และรายงานใน plan.md
  - ขนาดหน้าตาม dpi จริง และตั้ง metadata Producer
- ทดสอบ (เทสต์ใหม่ `ScanSessionTests`, `ScanPdfBuilderTests`)
  - PDF ที่ได้เปิดด้วย PDFium ได้ จำนวนหน้าถูก
  - หน้าขาวดำ A4 300 dpi ไม่เกิน 100 KB
  - การหมุนและการตัดถูกต้อง
  - temp ถูกลบหลัง Dispose และหลัง exception

### ขั้น 4: WIA จริง (Codex `gpt-6.1-sol` · high)
- `src/Yrk.PdfStudio.App/Scanning/WiaScannerService.cs`
  - COM late-bound บนเธรด STA
  - ตั้งค่าโหมดสี (WIA_IPS_CUR_INTENT), dpi และแหล่งกระดาษ (`WIA_DPS_DOCUMENT_HANDLING_SELECT`)
  - วนดึงจากถาดป้อนจนได้ `WIA_ERROR_PAPER_EMPTY` (0x80210003)
  - แปลง HRESULT เป็น error ของ `IScannerService`
  - ถ้าเครื่องไม่รองรับค่าที่เลือก ให้ใช้ค่าใกล้ที่สุดแล้วแจ้งผู้ใช้
- ไม่มี WIA ในเครื่อง (เช่น Windows N ที่ปิดบริการ) ให้ขึ้นข้อความ ไม่แครช
- ทดสอบอัตโนมัติ: แปลง HRESULT ทุกค่าที่รู้จัก และกรณีไม่พบเครื่องได้รายการว่าง
- ทดสอบด้วยมือ: Ken ทดสอบกับเครื่อง Epson ในขั้น 7

### ขั้น 5: หน้าต่างสแกน (Codex `gpt-6.1-sol` · medium)
- `Views/ScanDialog.xaml(.cs)` + `ViewModels/ScanViewModel.cs`
  - ส่วนตั้งค่า: เลือกเครื่อง, แหล่งกระดาษ, สี/เทา/ขาวดำ, dpi (150/200/300/600), ค่าบีบอัด
  - "สแกน" และ "สแกนเพิ่ม" ใช้สแกนหลายแผ่นต่อกัน มีแถบความคืบหน้าและปุ่มยกเลิก
  - รายการหน้าพร้อมภาพย่อ ใช้รูปแบบเดียวกับ `FileListDialog`
    - ลากสลับลำดับได้
    - ปุ่มหมุน ซ้าย/ขวา และลบ
    - ปุ่ม "ตัดขอบอัตโนมัติ" กับ "ลบหน้าว่าง"
  - กดภาพย่อสองครั้งเปิดหน้าตัดขอบที่ลากกรอบได้
  - ติ๊ก "ทำ OCR ภาษาไทย/อังกฤษ" ถ้ายังไม่มีไฟล์ภาษาให้ใช้ `EnsureLanguageDataAsync`
  - "สร้าง PDF…" เลือกที่บันทึก ถ้าทับไฟล์เดิมต้องยืนยัน แล้วเปิดไฟล์ใหม่ในโปรแกรม
- จำค่าตั้งล่าสุดในไฟล์ตั้งค่าเดิม แต่ไม่จำรูปหรือชื่อเครื่องที่ระบุตัวคนได้นอกจากชื่อรุ่น
- (เพิ่มหลังตรวจรอบ B) ตอนเปิดโปรแกรม ลบโฟลเดอร์ใน `%TEMP%\YRK PDF\scan\` ที่เก่ากว่า 1 วัน (เศษจากโปรแกรมปิดค้างหรือ Dispose ลบไม่ได้) ใช้กติกาเดียวกับ `ScanSession.DeleteOwnedPath` (ไม่ตามลิงก์ ลบเฉพาะใต้ `scan\`) + เทสต์
- (เพิ่มหลังตรวจรอบ B) ค่า dpi/โหมดที่เครื่องไม่รองรับแล้วใช้ค่าใกล้ที่สุด ต้องขึ้นข้อความบอกผู้ใช้ในหน้าต่าง
- ปุ่มที่หน้า Home (`MainWindow.Home.cs`) + แถบเครื่องมือ + `IDialogService.AskScan`
- ข้อความไทยใน `Localization/th.json` แล้ว `build\check-translations.ps1` ต้องยังได้ 12 คำเท่าเดิม
- ทดสอบ: เปิดหน้าต่างด้วย `UiDriver` + ตัวจำลอง แล้วเรนเดอร์ภาพหน้าต่างทั้งไทยและอังกฤษให้ Claude ดู

### ขั้น 6: OCR หลังสแกน + เทสต์ App ส่วน OCR (Codex `gpt-6.1-sol` · medium, เทสต์ `gpt-6-luna` · low)
- หลังสร้างไฟล์ ถ้าติ๊ก OCR ให้รัน `PdfOcr.RecognizeAsync` + `AddTextLayer` ทุกหน้า มีความคืบหน้าและยกเลิกได้
  - ถ้ายกเลิก ให้ได้ไฟล์ที่ไม่มีข้อความ ไม่ใช่ไฟล์เสีย
- เทสต์ App ใหม่ใน `tests/.../App/`
  - OCR จากเมนูเดิม: มีไฟล์ภาษาแล้ว / ต้องดาวน์โหลด / ยกเลิก
  - สแกน → OCR → ค้นข้อความไทยในไฟล์ได้
  - สแกนแล้วยกเลิกกลางทาง → temp ถูกลบ
- ทดสอบ: ทั้งชุดผ่าน

### ขั้น 7: ตรวจรวม (Claude + Codex read-only + Ken)
- Claude ตรวจ diff ส่วน temp/ลบไฟล์/เขียนทับไฟล์ และ `verify`
- Codex: `codex exec --sandbox read-only "ตรวจ git diff ตั้งแต่ <commit ต้นรุ่น> หาบั๊ก ช่องโหว่ และสิ่งที่ผิดจาก plan.md"`
- **Ken ทดสอบกับเครื่อง Epson**
  - สแกนจากกระจก 1 แผ่น และต่อกันหลายแผ่น
  - ถาดป้อน ถ้าเครื่องมี
  - สี/เทา/ขาวดำ ที่ 300 dpi
  - OCR ไทย
  - ถอดสาย USB กลางทาง ต้องขึ้นข้อความ ไม่แครช
- UiDriver เปิดทุกหน้าต่างใน Release

### ขั้น 8: เอกสาร (Codex `gpt-6-luna` · low)
- `docs/release-notes/1.2.2.md` (ไทย/อังกฤษ)
- `Directory.Build.props` → 1.2.2
- คู่มือ `docs/guide/th.html` + `en.html`: เพิ่มหัวข้อ "สแกนเข้า PDF"
- `build\build-guide.ps1`

### ขั้น 9: ปล่อยรุ่น (แชตหลักเท่านั้น, Ken ยืนยันทุกขั้น)
- ทำตาม `docs/release-checklist.md` ฉบับใหม่ รวมขั้น `verify`
- build หน้าเว็บใหม่ แล้ว Ken อัปโหลด
- อัปเดต PROJECT_NOTES

## ลำดับส่ง implementer
รอบ A: ขั้น 1–2 → รอบ B: ขั้น 3–4 → รอบ C: ขั้น 5–6 (ขั้น 0 และ 7–9 ทำในแชตหลัก)

## การตรวจผล (ทั้งหมด)
- `dotnet test` ทั้งชุดผ่าน และ `check-translations` ได้ 12 คำ
- สแกนจริงด้วย Epson ได้ PDF ที่เปิดใน YRK และ Acrobat ได้ และค้นคำไทยได้เมื่อติ๊ก OCR
- ไม่มีไฟล์รูปค้างใน temp หลังปิดหน้าต่าง ทั้งตอนปกติ ยกเลิก และ error
- `UpdateSigner verify` ผ่านกับแพ็กเกจ 1.2.2 ที่ปล่อย
- ค้นแล้วไม่มีลิงก์ github.io เหลือในโปรแกรมและคู่มือ

## สถานะ
- [ ] ขั้น 0 merge Dependabot (2026-11-13: merge #2, #1, #3, #5 แล้ว, #4 ปิดไว้แล้ว; เทสต์ผ่าน 1516 ข้าม 1; รอ Ken เปิด PDF ที่มีลายเซ็น, DWG จริง และลอง OCR)
- [x] ขั้น 1 verify + checklist (verify กับแพ็กเกจ 1.2.1 จริงผ่าน exit 0; เทสต์ใหม่ UpdateSignerVerifyTests)
- [x] ขั้น 2 ลิงก์ + log (เทสต์ทั้งชุดผ่าน 1535 ข้าม 1)
- [x] ขั้น 3 แกนสแกน (ขาวดำใช้ CCITT G4 ผ่าน PDFsharp 6.2.4 ได้จริง; หน้า A4 300 dpi ขาวดำ = 8.75 KB; PDFium เปิดและเรนเดอร์ได้ รวมไฟล์ผสมสี/ขาวดำ)
- [x] ขั้น 4 WIA (ทดสอบเฉพาะแปลง HRESULT และรายการเครื่องว่าง ยังไม่เคยลองกับเครื่องจริง รอ Ken ขั้น 7; การยกเลิกจะรอ Transfer ปัจจุบันจบก่อน)
- [x] ขั้น 5 หน้าต่างสแกน (ScanDialog + ScanCropDialog + MainViewModel.ScanToPdf; ล้างโฟลเดอร์ scan เก่าตอนเปิดโปรแกรม; แจ้งค่าที่เครื่องไม่รองรับในหน้าต่าง; ยกเลิก/error กลางทางเก็บหน้าที่สแกนแล้ว; ยังไม่ได้ลองกับเครื่องสแกนจริง; OCR รอขั้น 6)
- [x] ขั้น 6 OCR หลังสแกน + เทสต์ App (OCR รันบนไฟล์ที่สร้างแล้ว ยกเลิก/error/ไม่โหลดภาษา = ได้ PDF ไม่มีข้อความ; เทสต์ใหม่ AppOcrTests 9 ข้อ; เทสต์ Tesseract จริงข้ามถ้าไม่มีไฟล์ภาษา)
- [ ] ขั้น 7 ตรวจรวม + Ken ทดสอบ Epson
- [ ] ขั้น 8 เอกสาร
- [ ] ขั้น 9 ปล่อยรุ่น
