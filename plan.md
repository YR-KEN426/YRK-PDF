# แผน: เครื่องมือตรวจอัตโนมัติบน GitHub (ชั้น 1 ของแผนตรวจก่อนลง Store)

## ที่มา
Ken ขอให้ตรวจช่องโหว่และบั๊กทั้งหมดก่อน 2.0.0 (PROJECT_NOTES "แผนตรวจก่อนลง Store") ชั้น 1 ให้ตั้งตอนนี้ เพื่อให้เจอปัญหาเรื่อยๆ ไม่ใช่มากองตอนท้าย งานนี้ไม่ออกรุ่นโปรแกรม
สภาพตอนนี้: repo `YR-KEN426/YRK-PDF` เป็นสาธารณะ ยังไม่มี `.github/workflows/` เลย (มีแค่ ISSUE_TEMPLATE, FUNDING) ใช้ .NET 10 (`YrkPdfStudio.slnx`) App เป็น WPF (`net10.0-windows`)

## ขอบเขต
ทำ: CodeQL, Dependabot, ตรวจแพ็กเกจที่มีช่องโหว่, gitleaks, .NET analyzers, BinSkim
ไม่ทำ: แก้คำเตือนที่เครื่องมือเจอ งานนี้แค่จดเป็นรายการแล้วแบ่งลงรุ่น (ส่วนใหญ่ไป 1.2.5) ยกเว้นความลับที่หลุดจริง ซึ่งต้องจัดการทันที

## ขั้นตอน

### ขั้น 0: สแกนความลับในประวัติ git ในเครื่องก่อน (Claude)
- เหตุผล: log ของ GitHub Actions ใน repo สาธารณะ ใครก็เปิดดูได้ จึงต้องรู้ผลในเครื่องก่อนจะให้ CI สแกน
- ดาวน์โหลด gitleaks ตัวล่าสุด (`gitleaks_<ver>_windows_x64.zip`, ประมาณ 7–8 MB จาก github.com/gitleaks/gitleaks/releases, สัญญาอนุญาต MIT) ลง scratchpad และตรวจ checksum
  - **การอนุมัติแผนนี้ถือว่า Ken อนุญาตให้ดาวน์โหลดไฟล์นี้**
- รัน `gitleaks git --redact --report-path <scratchpad>` กับทุก commit
- ผลที่ได้: รายการที่เจอ เป็นแค่ชื่อไฟล์ + ชนิดของความลับ ไม่แสดงค่าจริง แล้วแยกเป็น 2 กลุ่ม
  - ของทดสอบ เช่น ใบรับรองทดสอบใน tests/ และ tools/LtvTestSigner → ใส่ใน allowlist
  - ของจริง → หยุดแล้วแจ้ง Ken ให้เปลี่ยนกุญแจหรือรหัสนั้นก่อน (กุญแจเซ็นอัปเดตต้องตรวจเป็นพิเศษ)

### ขั้น 1: เปิด .NET analyzers (Claude แก้เอง เพราะไม่เกิน 30 บรรทัดในไฟล์เดียว)
- `Directory.Build.props`: `AnalysisLevel` = `latest-recommended` และ `AnalysisModeSecurity` = `All` (กฎความปลอดภัย CA2xxx/CA3xxx/CA5xxx ครบทุกข้อ) ให้เป็นแค่คำเตือน ไม่ทำให้ build พัง
  - เลือกระดับนี้แทน latest-all เพราะ latest-all จะขึ้นคำเตือนเรื่องรูปแบบโค้ดเป็นพันข้อ จนคำเตือนด้านความปลอดภัยจมหายไป
- build Release แล้วนับคำเตือนแยกตามรหัสกฎ ส่งตัวเลขสรุปให้ Ken ไม่แปะ log ยาวๆ
- ทดสอบ: `dotnet build -c Release` ผ่าน และเทสต์ทั้งชุดยังผ่านเท่าเดิม (896+)

### ขั้น 2: workflow และ Dependabot (Codex `gpt-6.1-sol` · medium)
แก้หรือสร้างได้เฉพาะไฟล์เหล่านี้:
- `.github/dependabot.yml`
  - ตรวจ `nuget` และ `github-actions` ทุกสัปดาห์ รวมการอัปเดตเล็ก (minor/patch) เป็น PR เดียว
  - ignore `PDFsharp` เพราะตั้งใจล็อกไว้ที่ `[6.2.4]`
- `.github/workflows/codeql.yml`
  - C# แบบ `build-mode: none` (ไม่ต้อง build บน Windows จึงเร็ว) และ `actions` (ตรวจตัว workflow เอง)
  - query suite `security-extended`
  - รันเมื่อ push เข้า main, เมื่อมี PR และทุกสัปดาห์
- `.github/workflows/security.yml` รันบน `windows-latest` มี 3 งาน
  1. `dotnet list package --vulnerable --include-transitive` ถ้าเจอให้ job fail
  2. gitleaks: ดาวน์โหลดตัว CLI แบบล็อกรุ่นพร้อมตรวจ checksum (ไม่ใช้ gitleaks-action) รันด้วย `--redact` และใช้ `.gitleaks.toml`
  3. BinSkim: build Release แล้วสแกนเฉพาะ `Yrk*.dll/exe` และ `YrkEasyPdf.exe` ไม่สแกน DLL ของบุคคลที่สาม (PDFium, Tesseract) ส่งผลเป็น SARIF ขึ้น Code scanning
- `.gitleaks.toml`: allowlist ของทดสอบตามผลขั้น 0
- กติกาทุก workflow
  - `permissions:` ให้น้อยที่สุด (ค่าเริ่มต้น `contents: read` ให้ `security-events: write` เฉพาะ job ที่อัปโหลด SARIF)
  - ล็อก action ด้วย commit SHA พร้อมเขียนรุ่นไว้ในคอมเมนต์
  - ไม่ใช้ `pull_request_target` และไม่ใช้ secrets ใดๆ
- ก่อนเลือกรุ่น ให้ตรวจว่าแต่ละเครื่องมือยังมีคนดูแลอยู่ (CodeQL, gitleaks MIT, BinSkim MIT) แล้วจดรุ่นที่ใช้ไว้ในสรุป
- ทดสอบในเครื่อง
  - ตรวจ YAML ด้วย `actionlint` ถ้ามี (ไม่มีก็ข้ามแล้วบอก)
  - รันคำสั่งของ job 1 และ 3 ในเครื่องให้ผ่าน

### ขั้น 3: Claude ตรวจ แล้วให้ Ken อนุมัติก่อน push
- Claude ตรวจ `git diff`: permissions, การล็อก SHA, ไม่มีจุดที่ทำให้ log เผยความลับ, trigger ถูกต้อง
- Codex read-only ตรวจอีกมุม เพราะเป็นงานด้านความปลอดภัย
- commit ทีละขั้น **ห้าม push จนกว่า Ken จะยืนยัน**
- **Ken ทำเองในหน้า Settings → Code security ของ repo** (เป็นการเปลี่ยนค่าบัญชี Claude ทำแทนไม่ได้)
  - เปิด Dependabot alerts และ Dependabot security updates
  - เปิด Secret scanning และ Push protection
  - เปิด Private vulnerability reporting
  - Code scanning ไม่ต้องตั้งแบบ Default setup เพราะเราใช้ workflow เอง

### ขั้น 4: ดูผลรอบแรกแล้วแบ่งลงรุ่น (Claude)
- หลัง push ให้ดูผลรอบแรกของทุก job (ใช้ scout สรุปถ้าผลยาว)
- จดรายการที่เจอลง PROJECT_NOTES โดยไม่ใส่ค่าความลับ แบ่งรุ่นแบบนี้
  - ช่องโหว่ร้ายแรง → 1.2.2
  - ที่เหลือ → 1.2.5 (รุ่นตรวจทั้งหมด)
- ติ๊กชั้น 1 ในแผนตรวจก่อนลง Store

## การตรวจผล (ทั้งหมด)
- `dotnet build -c Release` และ `dotnet test` ผ่านเท่าเดิม
- บนแท็บ Actions ทุก workflow เป็นสีเขียว ยกเว้นตัวที่ตั้งใจให้ fail เมื่อเจอปัญหาจริง
- แท็บ Security → Code scanning มีผลทั้งจาก CodeQL และ BinSkim
- Dependabot เปิด PR แรก หรือขึ้นว่าไม่มีอะไรต้องอัปเดต
- log ของ gitleaks ไม่มีค่าความลับจริง (ตรวจว่าใช้ `--redact` แล้ว)

## หมายเหตุ
- `plan.md` เดิม (1.2.1) ปล่อยรุ่นแล้ว จะเขียนแผนนี้ทับ ของเดิมยังอยู่ในประวัติ git
- PR จาก Dependabot ที่แตะ Velopack หรือ System.Security.Cryptography.Pkcs ต้องให้ Claude ตรวจก่อน merge ทุกครั้ง เพราะเกี่ยวกับการอัปเดตและการเข้ารหัส

## สถานะ
- [x] ขั้น 0 สแกนความลับในเครื่อง (2026-11-12: gitleaks 8.30.1 สแกน 310 commit ไม่เจอเลย, กุญแจอัปเดตในประวัติเป็น PUBLIC KEY อย่างเดียว, ไม่มีบรรทัด PRIVATE KEY ในประวัติ; ขั้น 2 จึงยังไม่ต้องมี allowlist ใน .gitleaks.toml ใส่เมื่อเจอของทดสอบจริงเท่านั้น)
- [x] ขั้น 1 .NET analyzers (2026-11-12: build Release ไม่มี error, คำเตือนเหลือ 234 ข้อหลังปิด CA1707 เฉพาะโปรเจกต์เทสต์ (639 ข้อ ชื่อเมธอดเทสต์); มาก: CA1806 64, CA1859 33, CA1861 31, CA1822 22, CA1826 14, CA1068 12, CA1305 10; ด้านความปลอดภัย: CA5351 MD5 6 จุดใน `CosCrypt.cs` (ตัวเข้ารหัส PDF แบบเก่าตามมาตรฐาน ต้องคงไว้เพื่อเปิดไฟล์เก่า), CA5350 SHA1 1 จุดใน `PdfSignatureVerifier.cs:1323` (ตรวจลายเซ็นเก่า), CA2101 5 จุดใน `PdfiumNative.cs` (marshaling string ของ P/Invoke) ที่เหลืออยู่ในเทสต์; NU1900 ตรวจช่องโหว่ NuGet ในเครื่องไม่ได้เพราะต่อ api.nuget.org ไม่ได้ ให้ CI ตรวจแทน; เทสต์ผ่าน 1516 ข้าม 1 → แบ่งลงรุ่นในขั้น 4)
- [x] ขั้น 2 workflow + Dependabot (2026-11-12: สร้าง dependabot.yml, codeql.yml, security.yml; ยังไม่มี .gitleaks.toml เพราะขั้น 0 ไม่เจอความลับ; ยังไม่ push)
- [ ] ขั้น 3 ตรวจ + Ken อนุมัติ + Ken เปิดค่าใน Settings + push
- [ ] ขั้น 4 ดูผลรอบแรกและแบ่งลงรุ่น
