# Security policy

## Reporting a vulnerability

Please **do not** report security problems in a public issue.

- **GitHub:** use "Report a vulnerability" on the Security tab of
  [YR-KEN426/YRK-PDF](https://github.com/YR-KEN426/YRK-PDF/security/advisories/new)
  (private vulnerability reporting).
- **Email:** yrken.official@gmail.com, with "Security" in the subject.

Useful to include: the version (? Help ▸ About YRK PDF), what an attacker can do, and the steps or a sample file
that shows it. Do not send real confidential documents; a small test file that shows the problem is enough.

YRK PDF is maintained by one person in their own time. You will get an answer, usually within
7 days. A fix is released as a normal signed update, and the reporter is credited in the release notes
unless they prefer not to be.

## Supported versions

Only the latest release gets security fixes. Installed copies update themselves when update checks are
on (? Help ▸ About YRK PDF); otherwise download the latest version from the
[download page](https://yr-ken426.github.io/yrk-easy-pdf-release/).

## What is in scope

- Opening a crafted PDF, DWG/DXF or image file that makes the program run code, write files elsewhere,
  or crash in a way that loses the user's work
- Anything that sends a document, or information about it, off the computer without the user asking
- The update process: an update that installs without a valid signature from YRK's update key
- Digital signatures: a changed document that is still shown as validly signed, or a signature that
  the program creates incorrectly
- Redaction that leaves the removed content recoverable in the saved file

## รายงานช่องโหว่ด้านความปลอดภัย (ภาษาไทย)

กรุณา **อย่า** แจ้งช่องโหว่ใน Issue สาธารณะ ให้แจ้งแบบส่วนตัวผ่านปุ่ม "Report a vulnerability" ในแท็บ Security
ของ repository บน GitHub หรือส่งอีเมลถึง yrken.official@gmail.com โดยใส่คำว่า "Security" ในหัวเรื่อง
พร้อมเลขรุ่นของโปรแกรม ผลกระทบ และขั้นตอนหรือไฟล์ตัวอย่าง (อย่าส่งเอกสารลับจริง) ผู้พัฒนาจะตอบกลับโดยปกติภายใน 7 วัน
และแก้ไขผ่านการอัปเดตที่เซ็นรับรองตามปกติ
