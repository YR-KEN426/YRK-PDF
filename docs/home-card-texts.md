# ข้อความการ์ดหน้าแรก

ร่างตาม `HomeTools()` เรียงตามลำดับในโค้ด คอลัมน์ "คำอธิบายสั้น" ตรงกับโค้ดหลัง `4b2b55a` (14 ใบย่อให้พอดี 4 คอลัมน์) ส่วนคำอธิบายเดิมที่ยาวกว่าย้ายไปเป็นบรรทัดที่ 2 ของ tooltip (`FullKey` ใน `HomeTool`; ไม่แสดงถ้าภาษานั้นพูดเหมือนกับคำอธิบายสั้น) ดังนั้น tooltip มี: ชื่อ, คำอธิบายเดิม (ถ้าต่างจากคำอธิบายสั้น), แล้วบรรทัดข้อมูลเสริมจากตาราง

| id และหมวด | ชื่อการ์ด (ไทย / อังกฤษ) | คำอธิบายสั้นบนการ์ด (ไทย / อังกฤษ) | Tooltip บรรทัด 2–3 (ไทย / อังกฤษ) | ปุ่มลัด | อ้างอิงที่มา |
|---|---|---|---|---|---|
| `combine` · organize | รวม PDF / Combine PDF | หลายไฟล์เป็นไฟล์เดียว / Several files, one PDF | ลากเรียงลำดับไฟล์ก่อนรวมได้ / Drag files to set their order before combining. | - | `Views/CombineFilesDialog.xaml`; README.md (Combine PDFs) |
| `split` · organize | แยก PDF / Split PDF | แยกหน้าเป็นไฟล์ใหม่ / Save pages separately | ระบุช่วงหน้า หรือแยกทุก N หน้าได้ / Enter page ranges or split every N pages. | - | `Views/SplitDialog.xaml` |
| `pages` · organize | จัดหน้า / Organize pages | จัดการหน้า PDF / Manage PDF pages | หมุน ลบ ย้าย แทรก และแยกหน้าได้ / Rotate, delete, move, insert, and extract pages. | - | README.md (page tools); `MainWindow.Home.cs` (case `pages`) |
| `scanpdf` · organize | สแกนเข้า PDF / Scan to PDF | สแกนกระดาษเป็น PDF / Scan paper into PDF | สแกนต่อหลายหน้า และเลือก OCR ได้ / Scan more pages and optionally recognize text. | - | `Views/ScanDialog.xaml`; `MainWindow.Home.cs` (case `scanpdf`) |
| `scan` · organize | จัดหน้าสแกน / Clean up scans | ปรับภาพหน้าสแกน / Adjust scanned pages | ปรับเอียง ตัดขอบมืด และลบหน้าว่าง / Straighten, trim dark edges, and remove blank pages. | - | `Views/ScanCleanupDialog.xaml`; `MainWindow.Home.cs` (case `scan`) |
| `images` · organize | รูปภาพเป็น PDF / Images to PDF | รวมรูปเป็น PDF / Pictures into one PDF | รองรับรูป JPG และ PNG / Accepts JPG and PNG images. | - | `MainWindow.Home.cs` (HomeTools `images`); README.md (Images to PDF) |
| `batch` · organize | ทำทีละหลายไฟล์ / Process many files | ทำงานกับทั้งโฟลเดอร์ / Process a whole folder | บีบอัด แปลง หรือใส่เครื่องหมายหลายไฟล์ / Compress, convert, or mark many files. | - | `Views/BatchDialog.xaml`; `MainWindow.Home.cs` (HomeTools `batch`) |
| `convertscan` · convert | แปลงเอกสารสแกน / Convert scanned documents | แปลงสแกนเป็นไฟล์ใช้ต่อ / Convert scans for reuse | เลือก PDF ค้นข้อความได้, Word หรือข้อความ / Choose searchable PDF, Word, or text output. | - | `Views/ConvertScanDialog.xaml`; `MainWindow.Home.cs` (HomeTools `convertscan`) |
| `word` · convert | PDF เป็น Word / PDF to Word | ข้อความแก้ไขได้ / Editable text | ตารางและรูปภาพคงตำแหน่งโดยประมาณ / Tables and pictures stay in place. | - | README.md (PDF to Word) |
| `excel` · convert | PDF เป็น Excel / PDF to Excel | ตารางเป็นเซลล์ / Tables become cells | จำนวนเงินส่งออกเป็นตัวเลขที่นำไปคำนวณได้ / Amounts are exported as numbers you can add up. | - | README.md (PDF to Excel) |
| `powerpoint` · convert | PDF เป็น PowerPoint / PDF to PowerPoint | หน้าละหนึ่งสไลด์ / One slide per page | สไลด์มีหน้าตาเหมือน PDF / Slides look like the PDF pages. | - | README.md (PDF to PowerPoint) |
| `pictures` · convert | PDF เป็นรูปภาพ / PDF to images | แปลงแต่ละหน้าเป็นภาพ / Pages as JPG or PNG | เลือก PNG หรือ JPG ได้ / Choose PNG or JPG. | - | README.md (PDF to images) |
| `compress` · convert | บีบอัด PDF / Compress PDF | ทำให้ไฟล์เล็กลง / Make the file smaller | มี 3 ระดับ และบันทึกเป็นไฟล์ใหม่ / Choose from three levels and save a new file. | - | README.md (Compress PDF) |
| `ocr` · convert | แปลงภาพเป็นข้อความ (OCR) / Recognize text (OCR) | ค้นและคัดลอกจากภาพ / Make scans searchable | รองรับภาษาไทย; ตรวจทานผลจากภาพก่อนใช้ / Thai is supported; check recognized text before use. | - | README.md (OCR); `Views/OcrDialog.xaml` |
| `pdfa` · convert | PDF/A สำหรับเก็บถาวร / PDF/A for archiving | บันทึกเป็น PDF/A / Save as PDF/A | ใช้รูปแบบเก็บเอกสารระยะยาวที่หลายสำนักงานร้องขอ / A long-term archive format requested by many offices. | - | README.md (PDF/A for archiving) |
| `etax` · accounting | ใบกำกับภาษี (e-Tax Invoice) / e-Tax Invoice | สร้างเอกสารภาษี PDF/A-3 / Create tax invoices | รองรับใบกำกับภาษี ใบเพิ่มหนี้ และใบลดหนี้ พร้อม XML กรมสรรพากร / Supports tax invoices, debit and credit notes with Revenue Department XML. | - | `Views/ETaxInvoiceDialog.xaml`; README.md (e-Tax Invoice) |
| `amount` · accounting | จำนวนเงินเป็นตัวหนังสือ / Amount in words | แปลงยอดเงินเป็นคำ / Amounts as words | แสดงผลเป็นภาษาไทยและอังกฤษ / Produces Thai and English wording. | - | `Views/AmountWordsDialog.xaml`; `MainWindow.Home.cs` (HomeTools `amount`) |
| `edittext` · edit | แก้ไขข้อความ / Edit text | แก้คำใน PDF / Change words in a PDF | แก้ข้อความทีละบรรทัด ไม่จัดย่อหน้าใหม่ / Edits one line at a time; paragraphs do not reflow. | - | README.md (Text editing) |
| `addtext` · edit | เพิ่มข้อความ / Add text | พิมพ์ข้อความบนหน้า / Type on a page | ปรับแบบอักษรและขนาดได้ / Choose the font and size. | - | `MainWindow.Home.cs` (HomeTools `addtext`); README.md (Fill & Sign) |
| `addpicture` · edit | เพิ่มรูปภาพ / Add a picture | วางรูปบนหน้า / Place a picture | ย้าย หมุน และปรับความโปร่งใสได้ แก้ได้อีกหลังบันทึก / Move, turn and fade it; still editable after saving. | - | `MainWindow.Home.cs` (HomeTools `addpicture`); PROJECT_NOTES (1.1.9 หมุน/ความโปร่งใส, 1.2.1 แก้รูปที่บันทึกแล้ว) |
| `fill` · edit | กรอกแบบฟอร์ม / Fill in a form | กรอกหรือสร้างช่องฟอร์ม / Fill or create form fields | ใช้ได้กับช่องข้อความ ช่องติ๊ก ปุ่มเลือก และรายการให้เลือก / Works with text boxes, check boxes, radio buttons and drop-downs. | - | `MainWindow.Home.cs` (HomeTools `fill`); README.md บรรทัด 92–94 |
| `sign` · edit | เซ็น / Sign | วางลายเซ็น / Place your signature | วาด พิมพ์ชื่อ หรือใช้ภาพลายเซ็น / Draw, type, or use an image of your signature. | - | `Views/SignatureDialog.xaml` |
| `digital` · edit | ลายเซ็นดิจิทัล / Digital signature | เซ็นด้วย Digital ID / Sign with a Digital ID | แสดงเมื่อเอกสารถูกเปลี่ยนภายหลัง / Later changes are shown. | - | `Views/DigitalSignDialog.xaml`; README.md (Digital signatures) |
| `certified` · edit | สำเนาถูกต้อง / Certified true copy | รับรองสำเนาพร้อมลายเซ็น / Certify a copy | ใส่บรรทัด "ใช้สำหรับ…เท่านั้น" และวันที่ได้ / Add a "for … only" line and the date. | - | `Views/CertifiedCopyDialog.xaml`; README.md (Fill & Sign) |
| `stamp` · edit | ตรายาง / Rubber stamp | เลือกหรือปรับตรา / Choose or edit a stamp | เปลี่ยนข้อความ สี และวันที่ หรือบันทึกตราบริษัทไว้ใช้ซ้ำ / Change words, colour, and date, or save a company stamp for reuse. | - | `Views/StampDialog.xaml` |
| `comment` · edit | ไฮไลต์และความคิดเห็น / Highlight and comment | ทำเครื่องหมายและจดบันทึก / Mark and annotate | มีไฮไลต์ ขีดเส้นใต้ บันทึก และวาด / Includes highlighting, underlining, notes, and drawing. | - | `MainWindow.Home.cs` (case `comment`); README.md (Comments) |
| `marks` · edit | ลายน้ำและเลขหน้า / Watermark and page numbers | เพิ่มเครื่องหมายบนหน้า / Add marks to pages | เพิ่มลายน้ำ หัวกระดาษ ท้ายกระดาษ หรือเลขหน้า / Add a watermark, header, footer, or page numbers. | - | `MainWindow.Home.cs` (case `marks`); README.md (Page marks) |
| `dwg` · drawings | เปิดไฟล์ DWG และ DXF / Open DWG and DXF | ดูแบบไม่ต้องมี AutoCAD / View without AutoCAD | เปิดปิดเลเยอร์ได้ วัดระยะตามสเกลของแบบ / Switch layers on and off and measure at the drawing's scale. | - | README.md บรรทัด 27, 186–200 |
| `measure` · drawings | วัดระยะ / Measure | วัดตามสเกลแบบ / Measure to scale | การจับจุดช่วยหาปลายและกึ่งกลางเส้น / Snapping finds line ends and midpoints. | - | README.md (Measure and compare) |
| `count` · drawings | นับจำนวน / Count items | คลิกนับรายการบนแบบ / Click to count items | ส่งออกรายการนับได้ / Export the count list. | - | `MainWindow.Home.cs` (HomeTools `count`); README.md (Count items) |
| `compare` · drawings | เปรียบเทียบแบบ / Compare versions | ดูความต่างระหว่างฉบับ / Spot revision changes | เปรียบเทียบจากไฟล์ที่บันทึกไว้ จึงไม่รวมการแก้ไขที่ยังไม่บันทึก / Compares saved files, so unsaved changes are excluded. | - | README.md (Measure and compare) |
| `protect` · security | ใส่รหัสผ่านป้องกัน / Protect with password | ตั้งรหัสเปิดหรือจำกัดสิทธิ์ / Add a password | เลือกจำกัดการพิมพ์ คัดลอก แก้ไข และการทำงานอื่นได้ / Choose restrictions such as printing, copying, or editing. | - | `Views/ProtectDialog.xaml`; README.md (Protect with password) |
| `unprotect` · security | ปลดรหัสผ่าน / Remove password | บันทึกสำเนาที่ปลดล็อก / Save an unlocked copy | ไฟล์ที่จำกัดสิทธิ์ต้องใช้รหัสผ่านเจ้าของ / Files with restrictions require the owner password. | - | README.md (Remove password protection) |
| `redact` · security | ลบข้อมูลถาวร (ทึบดำ) / Black out (redact) | ลบข้อมูลส่วนตัวถาวร / Erase private details | ข้อความ ภาพ และความคิดเห็นใต้กล่องถูกลบจริง ทำเครื่องหมายทุกที่ที่คำค้นปรากฏได้ / Text, drawings and comments under the box are removed; can mark every match of a search. | - | README.md บรรทัด 172–173 |

## Claude ตรวจแล้ว (2026-11-18)

- แก้ 5 แถว: `addpicture` และ `fill` (tooltip เดิมไม่มีประโยชน์หรือซ้ำกับการ์ด), `certified` (เดิมเป็นรายละเอียดภายใน), `dwg` (เดิมพูดแต่ข้อจำกัด), `redact` (เดิมบอกว่าไฟล์มีรหัสผ่านลบไม่ได้ แต่ README บรรทัด 24 และ PROJECT_NOTES บอกว่าทำได้แล้ว; README บรรทัด 431 ยังเป็นข้อความเก่า ต้องแก้ในขั้นเอกสาร)
- ยืนยันกับโค้ดแล้ว: `count` ส่งออกได้ (`MainViewModel.Count.cs`), `images` รับ JPG/PNG, `pages`, `addtext`, `marks` ตรงกับ `HomeTools()`
- ปุ่มลัด: ไม่มีปุ่มลัดเฉพาะเครื่องมือ จึงไม่ใส่บรรทัดปุ่มลัดใน tooltip
- **รอ Ken ตรวจทั้งตาราง** ก่อนลงโค้ดในขั้น 4c

## หลังขั้น 4c (2026-11-19)

- คำอธิบายสั้นใหม่ 14 ใบ (split, images, pictures, ocr, addpicture, sign, certified, etax, amount, dwg, measure, compare, protect, redact) ตามคอลัมน์ที่ 3 ของตาราง
- คำอธิบายเดิมของ 14 ใบนี้อยู่ใน tooltip บรรทัดที่ 2 เช่น split: "เลือกหน้าเพื่อแยกไฟล์ / Save selected pages separately", redact: "ลบข้อมูลส่วนตัวถาวร / Permanently remove private details" (อ่านค่าเดิมจาก `FullKey` ใน `MainWindow.Home.cs`)
- การ์ด compress ใช้ "ทำให้ไฟล์เล็กลง" (คีย์ "Make the file smaller" มีอยู่แล้ว) แทน "ลดขนาดไฟล์"
- README บรรทัดเรื่อง redact ไฟล์ที่ป้องกันรหัสผ่านแก้ให้ตรงกับบรรทัด 24 แล้ว
