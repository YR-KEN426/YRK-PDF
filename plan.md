# แผน 1.1.8: แสดงความคืบหน้าการดาวน์โหลดอัปเดต

## ที่มา
Ken ขอ (2026-11-06): ให้ผู้ใช้รู้ว่าอัปเดตกำลังโหลดอยู่ และเน็ตค้างหรือไม่
- ตอนนี้ % ขึ้นเฉพาะตอนกด "ตรวจหาการอัปเดต" ในหน้าต่างเกี่ยวกับ ส่วนการตรวจเบื้องหลัง (quiet) ไม่แสดงอะไรเลยจนปุ่มรีสตาร์ทขึ้น
- รวมงานเล็กที่ค้าง: ComboBox จังหวัดใน e-Tax
- เมื่ออนุมัติ: เขียนแผนนี้ลง `plan.md` แทนแผน 1.1.7 (แผนเดิมดูได้จาก git `920d708`) แล้ว commit พร้อม `.gitignore` และ `PROJECT_NOTES.md` ที่แก้ค้างไว้

## สิ่งที่ผู้ใช้จะเห็น
- ใช้แถบเดิม `UpdateBanner` (`MainWindow.xaml:1179`) ซึ่งเห็นทั้งบนหน้า Home และในแท็บอยู่แล้ว ระยะกันใน `MainWindow.Home.cs:109` ใช้ได้ต่อโดยไม่ต้องแก้
- **กำลังโหลด:** "กำลังดาวน์โหลด YRK PDF 1.1.9… 42% (12.3 / 29.4 MB)" พร้อมแถบความคืบหน้าบางๆ
- **ไม่ขยับ 30 วินาที:** ข้อความเปลี่ยนเป็น "การดาวน์โหลดไม่คืบหน้า เน็ตอาจช้าหรือค้าง" และมีปุ่ม "ยกเลิก" ถ้าโหลดต่อได้ ข้อความกลับเป็นปกติเอง
- **หยุดเอง 5 นาที หรือกดยกเลิก:** แถบแสดง "การดาวน์โหลดหยุดแล้ว" พร้อมปุ่ม "ลองอีกครั้ง" และ "ปิด" ในรอบนี้โปรแกรมจะไม่โหลดซ้ำเอง (รอเปิดโปรแกรมครั้งหน้า หรือผู้ใช้กดลองอีกครั้ง)
- **โหลดเสร็จ:** แถบเดิม "YRK PDF {0} พร้อมติดตั้ง" + ปุ่มรีสตาร์ท เหมือนเดิม
- ช่วงตรวจ feed (ยังไม่รู้ว่ามีรุ่นใหม่) ไม่แสดงแถบ เพื่อไม่รบกวนผู้ใช้ที่ไม่มีอัปเดต
- ข้อจำกัด: ผู้ใช้เห็นแถบนี้ตั้งแต่อัปเดตจาก 1.1.8 ขึ้นไป

## ขั้นตอน

### ขั้น 1: ตัวติดตามความคืบหน้า + AppUpdater (Codex `gpt-6.1-sol` · medium)
ไฟล์: `src/Yrk.PdfStudio.App/Services/AppUpdater.cs`, ไฟล์ใหม่ `src/Yrk.PdfStudio.App/Services/UpdateDownloadProgress.cs`, `tests/Yrk.PdfStudio.Core.Tests/AppUpdaterTests.cs`, ไฟล์ใหม่ `tests/Yrk.PdfStudio.Core.Tests/UpdateDownloadProgressTests.cs`
- `UpdateDownloadProgress`: คลาสล้วน ไม่มี timer รับเวลาจากผู้เรียก (นาฬิกาจำลองในเทสต์ = ส่งเวลาเอง)
  - `Report(int percent, DateTimeOffset now)` ขยับเวลาล่าสุดเฉพาะเมื่อ % เปลี่ยน (เหมือนกติกา 5 นาทีใน `AppUpdater.cs:91-103`)
  - `IsStalled(DateTimeOffset now)` จริงเมื่อไม่ขยับ ≥ 30 วินาที
  - `DownloadedBytes` จาก `percent × ขนาดไฟล์`
- `AppUpdater`:
  - เก็บขนาดไฟล์และเลขรุ่นที่กำลังโหลดจาก `update.TargetFullRelease` ให้ผู้เรียกอ่านได้
  - กดยกเลิก (token ของผู้เรียก) ต้องลบ `.partial` และไฟล์แพ็กเกจเหมือนกรณีหมดเวลา (`AppUpdater.cs:112`) แล้วตรวจใหม่ได้ทันที
  - ห้ามเปลี่ยนการตรวจลายเซ็นและกติกา 5 นาที
- เทสต์ (ตามที่ Ken ตกลง เล็ก 2–3 ข้อ + 1 ข้อของการลบไฟล์):
  1. ไม่ขยับ 30 วินาที → `IsStalled` จริง, 29 วินาที → เท็จ
  2. % ซ้ำไม่รีเซ็ต, % เปลี่ยนแล้วรีเซ็ต
  3. กดยกเลิกระหว่างโหลด → ไฟล์ที่ยังไม่ครบถูกลบ และเรียกโหลดใหม่ได้ (ใช้ `TrackingSource` / `UpdateFixture` เดิม)
  - ข้อ 3 ต้องทำ mutation check: ลบโค้ดลบไฟล์ออกแล้วเทสต์ต้อง fail

### ขั้น 2: ViewModel และแถบอัปเดต (Codex `gpt-6.1-sol` · medium)
ไฟล์: `src/Yrk.PdfStudio.App/ViewModels/MainViewModel.Updates.cs`, `MainViewModel.Tabs.cs`, `MainWindow.xaml`, `Localization/th.json`
- สถานะร่วมทุกแท็บแบบ static ตามแบบเดิม (`s_readyVersion` + event `UpdateReady`, ต่อ event ใน `MainViewModel.Tabs.cs:55-60`): สถานะ ไม่มี / กำลังโหลด / ค้าง / หยุด / พร้อม, ตัวติดตาม, `CancellationTokenSource` ตัวเดียว
- ส่ง progress ทั้งตอน quiet และไม่ quiet (ไม่ quiet ยังเขียน `StatusMessage` เหมือนเดิม) อัปเดต UI ผ่าน Dispatcher ไม่เกินประมาณ 4 ครั้งต่อวินาที
- `DispatcherTimer` 1 วินาทีขณะโหลด ใช้เช็ก `IsStalled` หยุด timer เมื่อจบ
- คำสั่งใหม่: ยกเลิก, ลองอีกครั้ง (เรียก `CheckForUpdatesAsync(quiet: true)`), ปิดแถบ
- XAML: ใน `UpdateBanner` เพิ่ม ProgressBar บาง, ปุ่มยกเลิก/ลองอีกครั้ง/ปิด, ปุ่มรีสตาร์ทเห็นเฉพาะสถานะพร้อม เปลี่ยน Visibility ของแถบเป็นเห็นเมื่อสถานะไม่ใช่ "ไม่มี"
- ข้อความใหม่ทั้งหมดใส่ `th.json` แล้วรัน `build\check-translations.ps1` (ผลปกติ: 4 คำไม่มีภาษาไทย)
- หน้าต่างเกี่ยวกับ (`AboutDialog.xaml.cs:74`) คงเดิม

### ขั้น 3: e-Tax ComboBox จังหวัด (Claude แก้เอง ไม่เกิน 30 บรรทัด + เทสต์ `gpt-6-luna` · low)
ไฟล์: `src/Yrk.PdfStudio.App/Views/ThaiAddressFields.xaml.cs` (+ `.xaml`), `tests/Yrk.PdfStudio.Core.Tests/ETaxTests.cs`
- ต้นเหตุ: เลือกจังหวัดเดิมซ้ำไม่เกิด `SelectionChanged` จึงไม่ล้าง `_autoSelectedAreas` (:14)
- แก้: ฟัง `DropDownClosed` ของ ComboBox จังหวัด/อำเภอ/ตำบล ถ้าผู้ใช้เปิดรายการแล้วปิด ถือว่ายืนยันค่านั้นเอง ล้างส่วนที่เลือกอัตโนมัติ (แยกเป็นเมธอดที่เทสต์เรียกได้)
- เทสต์: ต่อจาก `ETaxTests.cs:350-377` รหัสไปรษณีย์เลือกจังหวัด → ผู้ใช้ยืนยันจังหวัดเดิม → แก้รหัส → จังหวัดไม่เปลี่ยน

### ขั้น 4: เลขรุ่นและ release notes (Codex `gpt-6-luna` · low)
ไฟล์: `Directory.Build.props` (1.1.8), `docs/release-notes/1.1.8.md` (ไทย/อังกฤษตามแบบรุ่นก่อน), FAQ ในคู่มือถ้าพูดถึงการรออัปเดต
- การ build, เซ็น และปล่อยรุ่น: Ken ทำเองตามเช็กลิสต์

### ขั้น 5: ตรวจอีกมุม (Codex read-only)
- `codex exec --sandbox read-only -o <scratchpad>/review.md` ตรวจ diff ทั้งฟีเจอร์ เน้นการลบไฟล์ตอนยกเลิก, race ระหว่างยกเลิกกับโหลดเสร็จ, และการกดลองอีกครั้งซ้ำๆ

## การตรวจ
- ทุกขั้น: Claude อ่าน `git diff` + รัน `dotnet test` ข้างนอก sandbox (OutDir แยก เพราะ Ken อาจเปิดโปรแกรมอยู่)
- ขั้น 2: build Release แล้วเปิดดูแถบด้วยตา โดยใช้ feed ทดสอบในเครื่อง (`UpdateFixture` หรือ `build\make-tampered-feed.ps1` แบบไฟล์ถูกต้อง) ทั้งบน Home และในแท็บ ไทยและอังกฤษ — ต้องเช็กก่อนว่า Ken ไม่ได้เปิดโปรแกรมอยู่
- หลังปล่อย: ทดสอบอัปเดตจริง 1.1.8 → 1.1.9 ดูแถบด้วยตา (เช็กลิสต์เดิม)

## สถานะ
- [x] ขั้น 1: ตัวติดตามความคืบหน้า + AppUpdater + เทสต์
- [ ] ขั้น 2: ViewModel และแถบอัปเดต
- [ ] ขั้น 3: e-Tax ComboBox จังหวัด
- [ ] ขั้น 4: เลขรุ่นและ release notes
- [ ] ขั้น 5: Codex ตรวจอีกมุม
