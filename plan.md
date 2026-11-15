# แผน: ข้อความ error ทั้งโปรแกรม + ปุ่มคัดลอกข้อมูลแจ้งปัญหา + ตัวดักแครชกลาง + ตรวจข้อความสองภาษา (1.2.3)


## ที่มา
- ตอนนี้กล่อง error เป็น `MessageBox` ธรรมดา (`IDialogService.ShowError(string)` ที่ `Services/DialogService.cs:306`) มีจุดเรียกประมาณ 50 จุด และมีอีกประมาณ 60 จุดที่โชว์ `ex.Message` ดิบ ซึ่งเป็นภาษาอังกฤษเชิงเทคนิค บางที่มี path เต็มติดมาด้วย
- ยังไม่มีรหัส error ของโปรแกรมเอง มีแค่ HRESULT ในหน้าต่างสแกน (`Views/ScanDialog.xaml.cs:430–448`)
- ตัวดักแครชมีแค่ `DispatcherUnhandledException` (`App.xaml.cs:156`) ยังไม่ดัก exception จาก thread อื่นและจาก Task
- log เก็บ `ex.ToString()` เต็มๆ ซึ่งมี path และชื่อผู้ใช้อยู่ด้วย และยังไม่มีตัวลบข้อมูลส่วนตัวออก
- ระบบแปลภาษาใช้ข้อความอังกฤษเป็น key แล้วไปหาคำแปลใน `Localization/th.json` (`Loc.T` / `Loc.F` และ `UiTranslator` สำหรับ XAML) ถ้า th.json ไม่มีคำแปล ข้อความจะขึ้นเป็นอังกฤษโดยไม่มีอะไรเตือน
- สิ่งที่ Ken ตกลงไว้ใน PROJECT_NOTES:
  - แต่ละ error มีคำอธิบายไทยและอังกฤษที่อ่านเข้าใจง่าย บอกว่าต้องทำอะไรต่อ มีรหัสคงที่ และมีส่วน "รายละเอียด"
  - รวมข้อความไว้ใน catalogue กลางที่เดียว
  - ห้ามมี path หรือข้อมูลส่วนตัวในข้อความที่ผู้ใช้อาจส่งต่อ
  - ปุ่มคัดลอกต้องให้ผู้ใช้กดเองเท่านั้น ไม่ส่งอะไรออกอัตโนมัติ

## ของเดิมที่ใช้ต่อ
| ของเดิม | ที่อยู่ | ใช้ทำอะไร |
|---|---|---|
| `Loc.T` / `Loc.F` และ th.json | `Localization/Loc.cs` | แปลข้อความทุกอย่าง (ไม่สร้างระบบแปลใหม่) |
| `Feedback.ProgramInfo(version)` | `Services/Feedback.cs:28` | ข้อมูลรุ่นโปรแกรม Windows และภาษา ใส่ตอนต้นของข้อมูลแจ้งปัญหา |
| `AppUpdater.CurrentVersion` | `Services/AppUpdater.cs:81` | เลขรุ่น |
| `App.LogMessage` / `LogError` | `App.xaml.cs:112–114` | log เดิม ต่อยอดให้มีรหัส error ด้วย |
| `ScannerErrorKind` + `ErrorText` | `Core/Pdf/Scan/ScannerException.cs`, `ScanDialog.xaml.cs:430` | ย้ายข้อความ error สแกนเข้า catalogue (ยังแสดงในหน้าต่างสแกนเหมือนเดิม) |
| `ScriptedDialogs` | `tests/.../App/AppTestHarness.cs:117` | เทสต์ว่าเรียก error ถูกรหัส |
| เทสต์ log | `AppLogTests.cs`, `LogFolderTests.cs` | ใช้เป็นแบบของเทสต์ใหม่ |

## การออกแบบ
- **รหัส:** รูปแบบ `YRK-<ส่วน>-<เลข 3 หลัก>`
  - ส่วนที่ใช้: `FILE` (เปิด/บันทึก/กู้คืน), `PAGE`, `SIG` (ลายเซ็น/ใบรับรอง), `SEC` (รหัสผ่าน/ปิดทับ), `SCN`, `OCR`, `CNV` (แปลงเอกสารสแกน), `EXP` (ส่งออก/PDF/A), `PRN`, `BAT`, `UPD`, `CAD`, `EDIT` (แก้ข้อความ/รูป/คำอธิบาย/ฟอร์ม), `APP` (แครชที่ไม่ได้ดัก)
  - รหัสที่ใช้แล้ว ห้ามนำกลับมาใช้ใหม่ และห้ามเปลี่ยนความหมาย
- **catalogue:** `Services/Errors/ErrorCatalog.cs` เป็น static class เดียว
  - แต่ละรายการเป็น `ErrorInfo(Code, Message, NextStep)` โดย Message และ NextStep เป็นข้อความอังกฤษที่ใช้เป็น key ของ th.json
  - รองรับ `{0}` สำหรับค่าที่ไม่ใช่ข้อมูลส่วนตัว เช่น เลขหน้า
- **ตัวลบข้อมูลส่วนตัว:** `Services/Errors/ReportRedactor.cs` แทนสิ่งเหล่านี้ด้วย `<path>`, `<user>`, `<pc>`, `<email>`
  - path แบบมีไดรฟ์และ UNC (รวมชื่อไฟล์ท้าย path)
  - ชื่อผู้ใช้และชื่อเครื่อง
  - โฟลเดอร์ผู้ใช้
  - อีเมล
- **กล่อง error ใหม่:** `Views/ErrorDialog.xaml` แทน MessageBox มีองค์ประกอบดังนี้
  - ไอคอน
  - ข้อความหลัก
  - "สิ่งที่ทำได้: …"
  - รหัส (ตัวเล็ก เลือกคัดลอกได้)
  - Expander "รายละเอียด" แสดงชนิด exception, ข้อความเทคนิคที่ผ่านตัวลบข้อมูลส่วนตัวแล้ว และ HRESULT
  - ปุ่ม **"คัดลอกข้อมูลเพื่อแจ้งปัญหา"** และปุ่ม "ตกลง"
- **API:**
  - `IDialogService.ShowError(ErrorInfo error, Exception? ex = null, params object[] args)` แสดงกล่องและเขียน log โดยมีรหัสนำหน้า
  - เมื่อย้ายจุดเรียกครบแล้วให้ลบ `ShowError(string)` เดิมทิ้ง ไม่ให้มีทางกลับไปเขียนข้อความกระจายอีก
- **ข้อมูลแจ้งปัญหา:** `Services/Errors/ProblemReport.cs` สร้างข้อความเป็น `ProgramInfo` + รหัสและชนิด error (ถ้ามี) + log ล่าสุดไม่เกิน 40 บรรทัดจากไฟล์ของวันนี้ แล้วส่งทั้งหมดผ่าน `ReportRedactor` ก่อนใส่ clipboard
  - เรียกจากปุ่มในกล่อง error
  - เรียกจากรายการใหม่ในเมนูช่วยเหลือ (`MainWindow.xaml.cs:1602` วางต่อจาก "Suggestions and problems…") ซึ่งไม่มีรหัส มีแค่ log
- **ตัวดักแครชกลาง (`App.xaml.cs`):**
  - `DispatcherUnhandledException`: ใช้กล่อง error ใหม่กับรหัส `YRK-APP-001` แล้วทำงานต่อ (`Handled = true` เหมือนเดิม)
  - `AppDomain.UnhandledException`: เขียน log `YRK-APP-002` แล้วพยายามขึ้นกล่องสั้นๆ ก่อนโปรแกรมปิด
  - `TaskScheduler.UnobservedTaskException`: เขียน log `YRK-APP-003` + `SetObserved()` โดยไม่ขึ้นกล่อง
  - ตัวดักห้ามบันทึกหรือเขียนทับไฟล์ PDF ของผู้ใช้ ถ้ามีงานค้างให้ใช้ระบบกู้คืนเดิม (`MainWindow.Recovery.cs`)
  - กันวนซ้ำ: ถ้ากล่อง error เองพัง ให้ถอยไปใช้ MessageBox ธรรมดา
- **ตรวจข้อความสองภาษา:** เทสต์ `LocalizationCoverageTests` อ่านซอร์สแล้วหา literal เหล่านี้
  - ใน `Loc.T("…")`, `Loc.F("…")` และใน ErrorCatalog
  - ใน XAML เฉพาะ Text/Header/Content/ToolTip ที่ไม่ใช่ binding

  แล้วตรวจว่าทุกตัวมีคำแปลใน th.json พร้อมรายการยกเว้นที่ตั้งใจไม่แปล (เช่น ชื่อแบรนด์ หรือหน่วย)
- **ไม่อยู่ในแผนนี้:** คู่มือ (จะทำในขั้นเอกสารของ 1.2.3 โดยใช้ตารางจากขั้น 7), ที่คั่นหนังสือ, การแทรกหน้าที่สแกน, ComboBox จังหวัด (แยกแผน)

## ขั้นที่ 1: Catalogue + ตัวลบข้อมูลส่วนตัว + ข้อมูลแจ้งปัญหา (ส่วนแกน ไม่มี UI)
- ไฟล์ใหม่:
  - `Services/Errors/ErrorInfo.cs`
  - `Services/Errors/ErrorCatalog.cs`
  - `Services/Errors/ReportRedactor.cs`
  - `Services/Errors/ProblemReport.cs`
  - เทสต์ `tests/.../ErrorCatalogTests.cs`, `ReportRedactorTests.cs`, `ProblemReportTests.cs`
- ใน catalogue ตอนนี้ใส่แค่รหัส `APP-001..003` กับรหัสสแกนที่ย้ายมาจาก `ScanDialog.ErrorText` ก่อน ส่วนรหัสอื่นเพิ่มตอนย้ายจุดเรียกในขั้น 4–5
- ต้องได้:
  - รหัสไม่ซ้ำและตรงรูปแบบ
  - ทุก Message และ NextStep มีคำแปลใน th.json
  - ตัวลบข้อมูลส่วนตัวผ่านทุกกรณีต่อไปนี้: `C:\Users\ken\Desktop\ลับ.pdf`, UNC, path ที่มีช่องว่างหรือภาษาไทย, ชื่อผู้ใช้ปนในข้อความ, อีเมล
  - ProblemReport ไม่ส่ง log ทั้งไฟล์ และส่งไม่เกิน 40 บรรทัด
- ทดสอบ: `dotnet test --filter "ErrorCatalog|ReportRedactor|ProblemReport"`
- รุ่น: `gpt-6.1-sol` · high (ตัวลบข้อมูลส่วนตัวเป็นเรื่องความเป็นส่วนตัว **Claude ตรวจเอง**)

## ขั้นที่ 2: กล่อง error ใหม่ + ปุ่มคัดลอก + เมนูช่วยเหลือ + log มีรหัส
- ไฟล์:
  - ไฟล์ใหม่ `Views/ErrorDialog.xaml(.cs)`
  - `Services/IDialogService.cs`, `Services/DialogService.cs`
  - `MainWindow.xaml.cs` (เมนูช่วยเหลือ)
  - `App.xaml.cs` (`LogError(code, ex)`)
  - `Localization/th.json`
  - `tests/.../App/AppTestHarness.cs` (ให้ ScriptedDialogs บันทึก overload ใหม่)
- ต้องได้:
  - กล่องขึ้นภาษาตามโปรแกรม
  - ใช้คีย์บอร์ดได้: Enter = ตกลง, Esc = ปิด, Tab ไปถึงปุ่มคัดลอก
  - มี `AutomationProperties.Name`
  - Expander รายละเอียดปิดอยู่ตอนเริ่ม
  - กดปุ่มคัดลอกแล้วขึ้น "คัดลอกแล้ว" และสิ่งที่อยู่ใน clipboard ต้องผ่านตัวลบข้อมูลส่วนตัวแล้ว
  - บรรทัด log ขึ้นต้นด้วยรหัส
- ทดสอบ: เทสต์เดิมทั้งชุดผ่าน + เทสต์ใหม่ว่า ShowError(ErrorInfo) เขียน log ที่มีรหัส
- รุ่น: `gpt-6.1-sol` · medium

## ขั้นที่ 3: ตัวดักแครชกลาง
- ไฟล์: `App.xaml.cs`, `Localization/th.json`, เทสต์ `tests/.../App/CrashHandlerTests.cs`
- ต้องได้:
  - ดักครบทั้ง 3 จุดตามการออกแบบ
  - ถ้ากล่อง error พัง ให้ถอยไปใช้ MessageBox
  - ไม่มีโค้ดบันทึกไฟล์ผู้ใช้ใน handler
  - log มีรหัส `APP-00x`
- ทดสอบ:
  - เทสต์เรียก handler ตรงๆ ด้วย exception ปลอม แล้วตรวจ log และข้อความ
  - Ken ลองด้วยเมนูดีบักลับ (ถ้ามี) หรือ Claude ใช้ UiDriver โยน exception
- รุ่น: `gpt-6.1-sol` · high (เกี่ยวกับไฟล์ผู้ใช้ **Claude ตรวจเอง**)

## ขั้นที่ 4: ย้ายจุดเรียก ชุด A: ลายเซ็น ใบรับรอง ความปลอดภัย ปิดทับ อัปเดต สแกน OCR แปลงสแกน
- ไฟล์:
  - `MainViewModel.Signatures.cs`
  - `Views/DigitalSignDialog.xaml.cs` (ShowError ส่วนตัว 11 จุด)
  - `Views/TrustedCertificatesDialog.xaml.cs`, `Views/SignatureDialog.xaml.cs`
  - `MainViewModel.Security.cs`, `MainViewModel.Redaction.cs`
  - `MainViewModel.Updates.cs:256` (ยังเขียนแค่ log แต่ใส่รหัส UPD)
  - `ScanToPdf`, `ScanOcr`, `Ocr`, `ConvertScan*`, `OcrReviewViewModel`
  - `Views/ScanDialog.xaml.cs` (ให้ใช้ catalogue และแสดงรหัส YRK คู่กับ HRESULT)
  - `ErrorCatalog.cs`, `th.json`
- กติกา:
  - ทุกจุดใช้ `ShowError(ErrorCatalog.X, ex)`
  - ห้ามเอา `ex.Message` ไปต่อในข้อความหลัก (ให้อยู่ในรายละเอียดเท่านั้น)
  - ข้อความใหม่ทุกข้อต้องเพิ่มคำแปลใน th.json
- ทดสอบ: เทสต์เดิมที่ตรวจ `ScriptedDialogs.Calls` ผ่าน (แก้ให้ตรวจรหัสแทนข้อความ) + `WiaErrorMappingTests`
- รุ่น: `gpt-6.1-sol` · medium (Claude ตรวจส่วนลายเซ็น การปิดทับ และการอัปเดตเอง)

## ขั้นที่ 5: ย้ายจุดเรียก ชุด B: ที่เหลือทั้งหมด + ลบ `ShowError(string)`
- ไฟล์:
  - `MainViewModel.cs`
  - `MainViewModel.Pages/Batch/Editing/Export/Archive/Print/Compare/Cad/Count/Versions/Transforms/Pictures/Annotations/FieldLooks.cs`
  - `MainWindow.Recovery.cs`, `PrintViewModel`, `CompareViewModel`, `SplitDialog`, `Feedback.cs:53`
  - `MessageBox.Show` ที่ยังเหลืออยู่นอก DialogService
- ต้องได้:
  - ไม่มี `ShowError(string)` เหลือ (ลบออกจาก interface แล้ว)
  - ไม่มี `ex.Message` ในข้อความที่ผู้ใช้เห็น ยกเว้นใน ErrorDialog รายละเอียดและใน log
- ทดสอบ: เทสต์ใหม่ `ErrorUsageGuardTests` อ่านซอร์ส `src/Yrk.PdfStudio.App` แล้วตรวจสองข้อ
  - ไม่มี `ex.Message` ใน `Loc.F(` หรือ `ShowError(`
  - ไม่มี `MessageBox.Show` นอก DialogService และ ErrorDialog
- รุ่น: `gpt-6.1-sol` · medium

## ขั้นที่ 6: ตรวจข้อความสองภาษาครบ
- แก้ตามมา (6-fix): ข้อความไทยที่ฝังใน XAML (สำเนาถูกต้อง, ใช้สำหรับ, เหตุผลลายเซ็น, ตัวอย่างตราประทับ) ตามภาษา UI + เทสต์กันข้อความไทยฝัง
- ไฟล์: เทสต์ใหม่ `tests/.../LocalizationCoverageTests.cs`, `Localization/th.json`, รายการยกเว้นในเทสต์
- ต้องได้:
  - เทสต์ผ่าน = literal ทุกตัวที่ผู้ใช้เห็นมีคำแปลไทย
  - เพิ่มคำแปลที่ขาดให้ครบ
  - th.json ไม่มี key ซ้ำ
  - ค่าใน th.json ไม่ว่าง
  - `{0}` และ `{1}` ในคำแปลต้องครบเท่าต้นฉบับ
- ทดสอบ: `dotnet test --filter LocalizationCoverage` + เทสต์ทั้งชุด
- รุ่น: เขียนเทสต์ใช้ `gpt-6.1-sol` · medium, เติมคำแปลที่ขาดใช้ `gpt-6-luna` · low (Claude สุ่มตรวจคำแปล)

## ขั้นที่ 7: ตรวจคำผิดไทย/อังกฤษ + ตารางรหัส error
- ตรวจคำผิด:
  - Codex (`gpt-6-luna` · low, `--sandbox read-only`) อ่าน th.json และข้อความอังกฤษใน catalogue แล้วทำรายการคำที่น่าจะผิดหรือไม่สม่ำเสมอ (เช่น "ลายเซ็น" กับ "ลายเซ็นต์") ลง `docs/i18n-review.md`
  - Claude คัดรายการ แล้ว Ken อนุมัติก่อนแก้
- ตารางรหัส error:
  - เทสต์หรือสคริปต์สร้าง `docs/error-codes.md` จาก ErrorCatalog เป็นตาราง รหัส | ไทย | อังกฤษ | สิ่งที่ทำได้
  - มีเทสต์ตรวจว่าไฟล์ตรงกับ catalogue เสมอ
  - ขั้นเอกสารของ 1.2.3 ใช้ไฟล์นี้ทำภาคผนวกในคู่มือ
- รุ่น: `gpt-6-luna` · low

## ขั้นที่ 8: ตรวจ + Ken ลองจริง
- Claude ตรวจ diff ทั้งฟีเจอร์ เน้น ReportRedactor, ตัวดักแครช และข้อความ SIG/SEC/UPD
- Codex ตรวจอีกมุมด้วย `codex exec --sandbox read-only "ตรวจ git diff ของฟีเจอร์ error catalogue หาบั๊ก การรั่วของ path/ชื่อผู้ใช้ และสิ่งที่ผิดจาก plan.md"`
- Ken ลอง:
  - เปิดไฟล์เสีย
  - ใส่รหัสผ่านผิด
  - ถอดสายสแกนเนอร์
  - เซ็นด้วยใบรับรองหมดอายุ
  - กดคัดลอกข้อมูลแจ้งปัญหาแล้ววางใน Notepad ดูว่าไม่มี path หรือชื่อผู้ใช้
  - สลับภาษาแล้วดูกล่อง error ทั้งสองภาษา
- อัปเดต PROJECT_NOTES.md: ขีดงานที่เสร็จ และลบ "OCR รุ่นแม่น" ออกจาก 1.2.6

## การส่งงาน
- ส่ง implementer ทีละชุด: (1–2), (3–4), (5–6), (7) แล้วค่อยทำขั้น 8
- ทุกขั้นต้องรันเทสต์ทั้งชุดก่อน commit และ commit ขั้นละครั้ง

## สถานะ
- [x] ขั้น 1 Catalogue + ตัวลบข้อมูลส่วนตัว + ข้อมูลแจ้งปัญหา
- [x] ขั้น 2 กล่อง error ใหม่ + ปุ่มคัดลอก + เมนูช่วยเหลือ + log มีรหัส
- [x] ขั้น 3 ตัวดักแครชกลาง
- [x] ขั้น 4 ย้ายจุดเรียก ชุด A
- [x] ขั้น 5 ย้ายจุดเรียก ชุด B + ลบ ShowError(string)
- [x] ขั้น 6 ตรวจข้อความสองภาษาครบ
- [ ] ขั้น 7 ตรวจคำผิด + ตารางรหัส error
- [ ] ขั้น 8 ตรวจ + Ken ลองจริง

## วิธีทดสอบรวม
- `dotnet test` ทั้งชุดผ่าน รวมเทสต์ใหม่ 6 ชุด: ErrorCatalog, ReportRedactor, ProblemReport, CrashHandler, ErrorUsageGuard, LocalizationCoverage
- Ken ลองตามขั้น 8 ทั้งสองภาษา
