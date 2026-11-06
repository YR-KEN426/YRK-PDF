# แผน 1.1.9: ใบรับรองที่ฉันเชื่อถือ + หมุนรูปและปรับความโปร่งใส

## ที่มา
Ken ตัดสินใจ (2026-11-07):
- ทำ 3 เรื่องที่รอเลือก แบ่ง 2 รุ่น: **1.1.9** = ใบรับรองที่เชื่อถือ + หมุนรูป/ความโปร่งใส, **1.2.0** = ใบเพิ่มหนี้/ใบลดหนี้ + ส่วนลด (เขียนแผนแยกหลังปล่อย 1.1.9; เลขรุ่นไม่กระโดด จึงต่อจาก 1.1.9 เป็น 1.2.0) งานสำรอง/MSIX ที่เคยวางไว้ 1.2.0 เลื่อนเป็น 1.2.1 และงานย้ายสีไป ResourceDictionary เลื่อนไปหลังจากนั้น (รอ Ken ยืนยันลำดับ)
- ใบรับรองที่เชื่อถือ: เก็บเป็นรายการของ YRK เอง ไม่แตะที่เก็บใบรับรองของ Windows
- หมุน: จุดจับหมุนอิสระ (Shift = ทีละ 15°) + ปุ่มหมุน 90°
- ส่วนลด e-Tax (ของ 1.2.0): ทั้งรายบรรทัดและท้ายบิล
- เมื่ออนุมัติ: เขียนแผนนี้ลง `plan.md` แทนแผน 1.1.8 และบันทึกการตัดสินใจข้างบนใน `PROJECT_NOTES.md` แล้ว commit

## สิ่งที่ผู้ใช้จะเห็น

### ใบรับรองที่ฉันเชื่อถือ
- ลายเซ็นจาก Digital ID ที่สร้างเอง (self-signed) ตอนนี้ขึ้น "ไม่ทราบตัวตน" ในแผงลายเซ็นจะมีปุ่มใหม่ **"เชื่อถือใบรับรองนี้…"** ข้างปุ่ม "ใบรับรอง" (`MainWindow.xaml:1009`)
  - ขึ้นเฉพาะลายเซ็นที่เอกสารไม่ถูกแก้ ตัวตนเป็น Untrusted และใบรับรองเป็นแบบ self-signed (รอบนี้ไม่ทำกับใบรับรองที่ออกโดย CA ที่ไม่รู้จัก)
- กดแล้วขึ้นหน้าต่างยืนยัน: ชื่อ อีเมล องค์กร วันหมดอายุ และ **ลายนิ้วมือ SHA-256** พร้อมคำเตือนให้ตรวจลายนิ้วมือกับเจ้าของผ่านช่องทางอื่น (เช่น โทรถาม) ก่อนกดเชื่อถือ
- เชื่อถือแล้ว ลายเซ็นขึ้นสถานะใหม่ **"ถูกต้อง: คุณเลือกเชื่อถือผู้ลงนามนี้"** (แยกจาก "ถูกต้อง" ที่ Windows รับรอง) และบอกว่ามีผลเฉพาะใน YRK บนเครื่องนี้ โปรแกรมอื่น เช่น Acrobat ยังแสดงว่าไม่ทราบตัวตน
- หน้าต่าง **"ใบรับรองที่ฉันเชื่อถือ"** (เปิดจากเมนูเดียวกับการจัดการ Digital ID): ดูรายการ ลบ และนำเข้าไฟล์ `.cer`
- ฝั่งผู้เซ็น: หน้าข้อมูล Digital ID แสดงลายนิ้วมือ SHA-256 และปุ่ม "ส่งออกใบรับรอง (.cer)" ซึ่งมีแค่กุญแจสาธารณะ ส่งให้ผู้รับนำเข้าได้

### หมุนรูปและความโปร่งใส
- ใช้กับรูป ตรายาง และรูปลายเซ็นที่วางบนหน้า (สิ่งที่ยังไม่บันทึกลงหน้า)
- จุดจับหมุนกลมเหนือกรอบ ลากเพื่อหมุนรอบจุดกลาง กด Shift ให้ติดทีละ 15° ระหว่างลากแสดงองศา
- แถบเล็กข้างปุ่มลบ: ปุ่ม ↻ 90° และปุ่มความโปร่งใสที่เปิดแถบเลื่อน 10–100%
- ย่อ/ขยายยังคงสัดส่วนเหมือนเดิม และทำงานถูกเมื่อหมุนแล้ว
- Undo/Redo รวมการหมุนและความโปร่งใส
- บันทึกแล้วสิ่งที่เห็นในไฟล์ตรงกับที่วาง ทั้งใน YRK และ Acrobat รวมหน้าที่มี /Rotate

## ขั้นตอน

### ขั้น 1: Core ตรวจลายเซ็นด้วยรายการที่ผู้ใช้เชื่อถือ (Codex `gpt-6.1-sol` · high, ความปลอดภัย Claude ตรวจ)
- ไฟล์: `src/Yrk.PdfStudio.Core/Pdf/Signatures/PdfSignatureModels.cs`, `PdfSignatureVerifier.cs`, เทสต์ใหม่ `tests/Yrk.PdfStudio.Core.Tests/SignatureUserTrustTests.cs`
- `PdfVerifyOptions` เพิ่ม `UserTrustedCertificates` (public, ค่าเริ่มต้นว่าง) และ `PdfSignerIdentity.TrustedByUser`
- `CheckIdentity` (`PdfSignatureVerifier.cs:1368`): ตรวจกับ Windows ก่อนเหมือนเดิม ถ้าได้ Trusted จบ ถ้าผลมีแค่ `UntrustedRoot`/`PartialChain` และ signer เป็น self-signed ให้สร้าง chain รอบสองด้วย `CustomRootTrust` = ใบรับรองในรายการ ถ้าผ่านไม่มี flag ใดเลย → `TrustedByUser`
  - เทียบด้วยตัวใบรับรองทั้งใบ (SHA-256 ของ DER) ไม่เทียบชื่อ
  - หมดอายุ/เพิกถอน/ใช้งานผิดประเภท ยังเป็นผลเดิม (Expired/Revoked/Untrusted)
- สถานะ (`:1348`): `TrustedByUser` ได้ `Valid` เหมือน Trusted แต่เอกสารถูกแก้หรือลายเซ็นเสียยังเป็น Modified/Invalid
- ไม่เปลี่ยนการตรวจการเพิกถอนออนไลน์ (`MainViewModel.Signatures.cs:295` ยังเริ่มเฉพาะ Trusted)
- เทสต์: ไม่มีรายการ → IdentityUnknown; มีรายการ → Valid/TrustedByUser; ใบรับรองอื่นชื่อเหมือนกัน → ยัง IdentityUnknown; ใบในรายการหมดอายุ → Expired; แก้เอกสารหลังเซ็น → ไม่ใช่ Valid; ใบรับรองที่มี CA (ไม่ใช่ self-signed) ในรายการ → ไม่มีผล; เทสต์เดิมใน `SignatureTests.cs:39` ยังผ่าน

### ขั้น 2: App รายการเชื่อถือ + UI (Codex `gpt-6.1-sol` · medium)
- ไฟล์: `src/Yrk.PdfStudio.App/Services/TrustedCertificateStore.cs` (ใหม่), `ViewModels/SignatureInfoViewModel.cs`, `ViewModels/MainViewModel.Signatures.cs`, `MainWindow.xaml`, `Views/TrustCertificateDialog.xaml(.cs)` และ `Views/TrustedCertificatesDialog.xaml(.cs)` (ใหม่), หน้าต่างข้อมูล Digital ID ที่มีอยู่, `Localization/th.json` + ไฟล์อังกฤษ, เทสต์ `tests/.../TrustedCertificateStoreTests.cs`
- เก็บที่ `%LOCALAPPDATA%\YRK\EasyPdf\TrustedCertificates\<sha256>.cer` (DER, กุญแจสาธารณะเท่านั้น) แบบเดียวกับ `DigitalIdStore.cs:32`
  - โหลดแล้วข้ามไฟล์ที่เสีย ชื่อไฟล์ไม่ตรงกับ hash หรือมีกุญแจส่วนตัว
  - นำเข้า `.cer`/`.crt` เท่านั้น ไม่รับ `.pfx`
- ส่งรายการเข้า `PdfVerifyOptions` ทุกครั้งที่ตรวจ (`MainViewModel.Signatures.cs:280, 298`) และตรวจลายเซ็นใหม่ทันทีหลังเพิ่ม/ลบ
- ปุ่มและหน้าต่างตาม "สิ่งที่ผู้ใช้จะเห็น" ใช้รูปแบบคำสั่งเดียวกับ `ShowCertificateCommand` (`MainViewModel.Signatures.cs:41`) และ `DialogService`
- ข้อความ `StatusText` ใหม่ใน `SignatureInfoViewModel.cs:34` ทั้งไทยและอังกฤษ, ไอคอน/สีใช้แบบ Valid
- ส่งออก `.cer` ของ Digital ID ตัวเอง: `X509Certificate2.Export(Cert)` เท่านั้น
- เทสต์: เพิ่ม/ลบ/โหลดซ้ำ, ไฟล์ขยะถูกข้าม, `.cer` ที่ส่งออกไม่มีกุญแจส่วนตัว, ใน `tests/.../App/` (`AppTestHarness`) เซ็นด้วย ID ที่สร้างเอง → เชื่อถือ → สถานะเปลี่ยน → ลบ → กลับเป็นไม่ทราบ

### ขั้น 3: Core หมุนและความโปร่งใสตอนเขียนลงหน้า (Codex `gpt-6.1-sol` · high)
- ไฟล์: `src/Yrk.PdfStudio.Core/Pdf/IPdfEngine.cs` (`AddImageAsync` :113 และ `AddVectorAsync`, `AddPdfPageAsync`), `Pdf/Pdfium/PdfiumDocument.Editing.cs` (`AddImage` :336, `UnitSquareTo` :392, ส่วน vector/form :443, :521), `PdfiumNative.cs` ถ้าต้องเพิ่มฟังก์ชัน, เทสต์ `tests/.../PdfEditingTests.cs`
- เพิ่มพารามิเตอร์เลือกได้ `PlacementStyle(double RotationDegrees = 0, double Opacity = 1)` ค่าเริ่มต้นได้ผลเหมือนเดิมทุก byte
- หมุนรอบจุดกลางของ bounds ต่อจาก matrix ของ `UnitSquareTo` (ซึ่งจัดการ /Rotate ของหน้าอยู่แล้ว)
- ความโปร่งใส: รูปคูณ alpha ใน BGRA ก่อนสร้าง bitmap; vector ใช้ alpha ของสีเส้น/สีเติม; PDF-page (form object) ใช้ ExtGState `ca/CA` ถ้า PDFium ทำได้สะอาด ถ้าไม่ได้ให้รายงานกลับ แล้วรอบนี้ซ่อนแถบความโปร่งใสของลายเซ็นชนิดนี้
- เทสต์: รูปหมุน 90°/45° แล้วกรอบที่อ่านจาก `PdfiumDocument.Graphics.cs` ตรงที่คาด; หน้า /Rotate 90 + หมุน 30°; opacity 0.5 แล้วสีพิกเซลที่ render ผสมกับพื้นขาวถูก; vector หมุนแล้วกรอบถูก; ไม่ส่ง style แล้วผลเหมือนเดิม

### ขั้น 4: App หมุน/ความโปร่งใสของสิ่งที่วาง (Codex `gpt-6.1-sol` · high)
- ไฟล์: `ViewModels/PlacedItemViewModel.cs` (เพิ่ม `Rotation`, `Opacity`), `ViewModels/MainViewModel.Editing.cs` (`ResizeItem` :188, undo, `CommitOverlaysAsync` :638), `MainViewModel.SignatureArt.cs` (`WriteSignatureAsync` :84), `MainViewModel.PlacedText.cs` (คำบนตรายาง), `MainWindow.xaml` (:165-212 template + thumbs), `MainWindow.xaml.cs` (:1024-1057), ข้อความไทย/อังกฤษ, เทสต์ใน `tests/.../App/`
- จอ: `RenderTransform` หมุนรอบกลางกรอบ + `Opacity`; `Bounds` ยังเป็นกรอบก่อนหมุน
- ย่อ/ขยายเมื่อหมุน: แปลงระยะลากเป็นแกนของกรอบที่หมุนแล้ว และให้มุมตรงข้ามอยู่ที่เดิม
- ตรายางที่คำเขียนเป็นข้อความจริง: กรอบหมุนตาม และคำหมุนรอบจุดกลางเดียวกันด้วย rotation/opacity ที่มีใน `PdfTextWriter.cs:64, :132` อยู่แล้ว
- เทสต์: หมุน/โปร่งใส แล้ว undo/redo; หมุน 90° สี่ครั้งกลับที่เดิม; บันทึกแล้วไฟล์มีภาพหมุนตรงกรอบ; ตรายางคำจริงหมุนตรงกับกรอบ; `MainWindowLayoutTests` ยังผ่าน (แก้ XAML หน้าต่างหลัก)

### ขั้น 5: เลขรุ่น, release notes, คู่มือ (Codex `gpt-6-luna` · low)
- เลขรุ่น 1.1.9, `docs/release-notes/1.1.9.md` (ไทย/อังกฤษ), อัปเดต `docs/guide/th.html` และ `en.html` ส่วนลายเซ็นและการวางรูป

### ขั้น 6: ตรวจ (Claude + Codex read-only)
- Claude ตรวจความปลอดภัยขั้น 1–2 เอง: การตัดสินความเชื่อถือ, การเทียบใบรับรอง, ไฟล์ที่เก็บ, ข้อความเตือน
- `codex exec --sandbox read-only "ตรวจ git diff ของ 1.1.9 หาบั๊ก ช่องโหว่ และสิ่งที่ผิดจาก plan.md"`
- เปิดตัว Release ดูด้วยตา (บอก Ken ก่อนว่าจะมีหน้าต่างขึ้น สำรอง/คืน `*.json` ใน `%LOCALAPPDATA%\YRK\EasyPdf` ตามบทเรียน 1.1.8)

ส่ง Codex เป็นชุด: ขั้น 1–2 → ตรวจ → ขั้น 3–4 → ตรวจ → ขั้น 5

## การตรวจ
- ทั้งชุดเทสต์ผ่าน (`dotnet test`) ทุกขั้น
- Claude ดู `git diff` ทุกขั้น ส่วนความปลอดภัยอ่านละเอียด
- Ken ทดสอบเอง:
  - เซ็นด้วย Digital ID ที่สร้างเอง → เปิดไฟล์ → "ไม่ทราบตัวตน" → เชื่อถือ (ตรวจลายนิ้วมือตรงกับหน้าข้อมูล Digital ID) → "ถูกต้อง: คุณเลือกเชื่อถือ" → ลบในหน้าต่างรายการ → กลับเป็นไม่ทราบ
  - ส่งออก `.cer` แล้วนำเข้าในหน้าต่างรายการ
  - วางรูป ตรายาง ลายเซ็น หมุนอิสระ/90°/โปร่งใส บันทึก แล้วเปิดใน YRK และ Acrobat ให้ตรงกัน รวมไฟล์ที่หน้าหมุนอยู่
  - อัปเดต 1.1.8 → 1.1.9 จริง พร้อมดูแถบความคืบหน้าบน Home และในแท็บ (ค้างจาก 1.1.8)

## สถานะ
- [x] ขั้น 1: Core ตรวจด้วยรายการที่ผู้ใช้เชื่อถือ
- [x] ขั้น 2: App รายการเชื่อถือ + UI
- [ ] ขั้น 3: Core หมุน/ความโปร่งใส
- [ ] ขั้น 4: App หมุน/ความโปร่งใส
- [ ] ขั้น 5: เลขรุ่น, release notes, คู่มือ
- [ ] ขั้น 6: ตรวจความปลอดภัย, Codex ตรวจ, ดูตัว Release
