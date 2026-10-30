# YRK PDF

(เดิมชื่อ YRK Easy PDF / formerly YRK Easy PDF)

โปรแกรม PDF ฟรีสำหรับ Windows ทำในประเทศไทย อ่าน กรอกฟอร์ม แก้ข้อความ เซ็นชื่อ จัดการหน้า แปลง PDF เป็น Word / Excel / PowerPoint
อ่านข้อความจากภาพสแกน (OCR ภาษาไทยและอังกฤษ) และเปิดแบบ AutoCAD (DWG/DXF) ได้ในโปรแกรมเดียว
มีเครื่องมือสำหรับงานบัญชี: ออกใบกำกับภาษีอิเล็กทรอนิกส์ (e-Tax Invoice) จำนวนเงินเป็นตัวหนังสือ ตรายางพร้อมวันที่ พ.ศ. และจัดหน้าสแกน
ฟรีทุกความสามารถ ใช้ในบริษัทได้ ไม่มีโฆษณา ไม่ต้องสมัครบัญชี เอกสารอยู่ในเครื่องของคุณ ไม่ถูกส่งให้ AI หรือคลาวด์

A free PDF program for Windows, made in Thailand: read, fill in, edit and sign PDFs, organise pages, convert
PDF to Word, Excel and PowerPoint, recognise scanned text (Thai and English) and open AutoCAD drawings. For
accounting work it makes e-Tax Invoices, writes amounts in words, stamps documents with the Thai date and
cleans up scans. Every feature is free, also for business use. No ads, no account; documents stay on your computer and are never
sent to an AI or cloud service.

## ดาวน์โหลด / Download

**[yr-ken426.github.io/yrk-easy-pdf-release](https://yr-ken426.github.io/yrk-easy-pdf-release/)**, or the
newest file under [Releases](https://github.com/YR-KEN426/yrk-easy-pdf-release/releases/latest):

- `YRK.EasyPdf-win-Setup.exe`: ตัวติดตั้ง แนะนำสำหรับคนทั่วไป อัปเดตตัวเองได้ / installer, updates itself
- `YRK.EasyPdf-win-Portable.zip`: ไม่ต้องติดตั้ง แตกไฟล์แล้วใช้ได้เลย / portable, no installation

Windows 10 หรือ 11 แบบ 64 บิต / Windows 10 or 11, 64-bit.

## What this repository is

This repository holds the **releases** (installer, portable zip and the signed update feed that
installed copies check) and the download website. Every update package is signed with YRK's own update
key, and the program refuses an update whose signature does not match. The source code is at
[YR-KEN426/YRK-PDF](https://github.com/YR-KEN426/YRK-PDF) (Apache License 2.0).

## แจ้งปัญหาและความคิดเห็น / Feedback

- [Issues](https://github.com/YR-KEN426/YRK-PDF/issues/new/choose): แจ้งปัญหาหรือเสนอความคิด
  (เป็นข้อมูลสาธารณะ อย่าแนบเอกสารที่มีข้อมูลส่วนตัว) / bug reports and ideas (public: do not attach private documents)
- [แบบฟอร์มความคิดเห็น / Feedback form](https://docs.google.com/forms/d/e/1FAIpQLSd-H6KQaZ6cZFB6L73aECFCWo84fS7jO5z4AyMol7P8mzGuJQ/viewform)
  (ไม่ต้องล็อกอิน / no sign-in) หรืออีเมล / or email yrken.official@gmail.com
- ช่องโหว่ด้านความปลอดภัย แจ้งแบบส่วนตัว / security problems, privately:
  [Report a vulnerability](https://github.com/YR-KEN426/YRK-PDF/security/advisories/new)
  or yrken.official@gmail.com

## สนับสนุนผู้พัฒนา / Support

YRK PDF ฟรีสำหรับทุกคน รวมถึงใช้ในบริษัท และจะฟรีทุกความสามารถต่อไป ถ้าโปรแกรมช่วยให้งานของคุณง่ายขึ้น
สนับสนุนการพัฒนาได้ที่ [GitHub Sponsors](https://github.com/sponsors/YR-KEN426)
เงินสนับสนุนใช้เป็นค่าใบรับรองเซ็นโปรแกรม เวลาพัฒนา และค่าใช้จ่ายสำหรับทำ Platform อื่น (Windows on ARM, Android, macOS, iOS) ขอบคุณครับ

YRK PDF is free for everyone, including businesses, and will stay that way. If it saves you time, you can
support its development through [GitHub Sponsors](https://github.com/sponsors/YR-KEN426): code signing,
development time, and the costs of other platforms (Windows on ARM, Android, macOS, iOS). Thank you!

## How it is made

YRK PDF is built with .NET and open-source components under permissive licenses (PDFium, PDFsharp,
Tesseract, ACadSharp, Velopack and others); their license texts ship with the program (? Help ▸ About).
The code is developed with the help of AI coding tools; the maintainer reviews and tests every change and
signs every release. "No AI" means the program never sends your documents to an AI service.
