# Contributing to YRK PDF

Thank you for helping. YRK PDF is a free program maintained by one person, so small, focused
reports and changes are the easiest to act on.

## Reporting a bug or suggesting an idea

- **GitHub Issues** on [YR-KEN426/YRK-PDF](https://github.com/YR-KEN426/YRK-PDF/issues/new/choose)
  (Thai or English), or
- the [feedback form](https://docs.google.com/forms/d/e/1FAIpQLSd-H6KQaZ6cZFB6L73aECFCWo84fS7jO5z4AyMol7P8mzGuJQ/viewform)
  (no account needed), or email yrken.official@gmail.com. In the program: ? Help ▸ Suggestions and problems.

Please include the version (? Help ▸ About YRK PDF), Windows version, what you did, what happened and what you
expected. **Issues are public:** never attach real documents with personal or confidential data. If a
problem only shows with one file, make a copy with the private parts removed, or describe how the file
was made (program, "Print to PDF", scanner…).

Security problems go through the private channel in [SECURITY.md](SECURITY.md), not an issue.

## Changing the code

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Build and test (Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)):

   ```bash
   dotnet build YrkPdfStudio.slnx
   dotnet test YrkPdfStudio.slnx
   ```

3. Keep to the existing style: match the surrounding code, its naming and comment density. UI text is
   written in English and translated in `src/Yrk.PdfStudio.App/Localization/th.json`; run
   `powershell -File build/check-translations.ps1` to find texts without a Thai translation.
4. Add or update tests in `tests/` for behaviour you change, and try the change in the running program.
5. In the pull request, say what you changed, why, and how you tested it (with which kinds of PDF).

Ground rules that keep the program what it is:

- **Offline and private:** no telemetry, analytics, ads, accounts or cloud services. Anything that uses
  the network must be something the user asks for, and must be listed on the Privacy page.
- **No AI features that send documents anywhere.**
- **Licenses:** only dependencies under permissive licenses (MIT, BSD, Apache-2.0, zlib, SIL OFL…). No
  GPL/AGPL code. Do not upgrade PDFsharp past 6.2.4 (newer versions are AGPL). Add the license text of
  any new component to `licenses/` and a row to [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **Never commit secrets:** private keys, certificates with private keys (.pfx/.p12), tokens, passwords
  or real customer documents. Only the public update key (`Assets/update-public-key.pem`) belongs in the
  repository.

AI coding tools may be used, as they are in this project; you are responsible for the change and must
have read, understood and tested it.

By submitting a contribution you agree that it is licensed under the same license as the project
(Apache License 2.0, see the `LICENSE` file).

## การมีส่วนร่วม (ภาษาไทย)

แจ้งปัญหาหรือเสนอความคิดได้ที่ GitHub Issues ของ YRK-PDF แบบฟอร์มความคิดเห็น หรืออีเมล
yrken.official@gmail.com โปรดบอกเลขรุ่น รุ่นของ Windows สิ่งที่ทำ และสิ่งที่เกิดขึ้น **Issue เป็นข้อมูลสาธารณะ
อย่าแนบเอกสารที่มีข้อมูลส่วนตัวหรือข้อมูลลับ** ปัญหาด้านความปลอดภัยให้แจ้งแบบส่วนตัวตาม [SECURITY.md](SECURITY.md)
ผู้ที่ต้องการแก้โค้ด กรุณาเปิด Issue คุยก่อน แล้วส่ง Pull Request พร้อมบอกวิธีที่ทดสอบ
