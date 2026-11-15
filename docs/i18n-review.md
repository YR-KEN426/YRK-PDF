# รายการตรวจคำผิดและความไม่สม่ำเสมอ (th.json + ErrorCatalog)

สถานะ: แก้แล้ว (applied) ตามที่ Ken อนุมัติ ข้อที่ตัดทิ้งไม่แก้: 13, 15, 16, 17 (เรื่องสไตล์ล้วน)
ข้อที่ Ken แก้จากคำแนะนำ: 1, 2 (ถ้อยคำใหม่), 10 (คง "Digital ID"), 11 (ใช้ "ปิดทับ" ตลอด), 14 (key ใหม่)
ข้อ 3: "drawing" คือข้อมูลเส้นเวกเตอร์ของลายเซ็นที่บันทึก จึงแปลเป็น "ข้อมูลลายเส้นที่บันทึกไว้เสียหาย"
ข้อ 9: โค้ดปฏิเสธขนาดที่ใหญ่เกินพื้นที่สแกนด้วย จึงแก้ key เป็น "Enter a paper width and height that fit the scanner."
สร้างจากการตรวจของ Codex (gpt-6-luna, อ่านอย่างเดียว) แล้วคัดเหลือเฉพาะปัญหาจริง ตัดเรื่องสไตล์ออก

| # | key (English) | current text | suggested | reason |
|---:|---|---|---|---|
| 1 | The certificate could not be trusted. | เพิ่มใบรับรองที่เชื่อถือไม่ได้ | ไม่สามารถเชื่อถือใบรับรองนี้ได้ | ความหมายเพี้ยน: ต้นฉบับบอกว่าใบรับรองไม่ได้รับความเชื่อถือ (รหัส YRK-SIG-001) ไทยอ่านเป็น "ใบรับรองที่เชื่อถือไม่ได้" |
| 2 | The trusted certificates could not be updated. | ปรับปรุงใบรับรองที่เชื่อถือไม่ได้ | ปรับปรุงรายการใบรับรองที่เชื่อถือไม่สำเร็จ | กำกวมเหมือนข้อ 1: "ที่เชื่อถือไม่ได้" ดูเหมือนคุณสมบัติของใบรับรอง ไม่ใช่ผลของการปรับปรุง |
| 3 | The saved drawing is damaged. | ลายเซ็นที่บันทึกไว้เสียหาย | แบบวาดที่บันทึกไว้เสียหาย | "drawing" ไม่ใช่ "signature" ไทยซ้ำกับ key ถัดไป (The saved signature is damaged.) จนแยกความหมายไม่ได้ |
| 4 | PDF, and AutoCAD drawings (DWG, DXF) and Illustrator files (AI) | PDF, แบบ AutoCAD (DWG, DXF) และไฟล์ Illustrator (AI) | key: PDF, AutoCAD drawings (DWG, DXF) and Illustrator files (AI) | English key มีเครื่องหมายจุลภาคและ "and" ซ้อนกัน อ่านผิดไวยากรณ์ (ต้องแก้ key ใน XAML/โค้ดพร้อมกัน) |
| 5 | YRK PDF closed before these changes were saved: ... No deletes them. | ... ถ้าเลือก ไม่ จะลบสำเนาทิ้ง | key: "... If you choose No, they are deleted." ไทย: ถ้าเลือก "ไม่" จะลบสำเนาทิ้ง | English key ผิดไวยากรณ์ ("No deletes them") ไทยควรครอบ "ไม่" ด้วยเครื่องหมายคำพูดให้ชัดว่าเป็นชื่อปุ่ม |
| 6 | Show password | แสดงรหัส | แสดงรหัสผ่าน | ที่อื่นใช้ "รหัสผ่าน" แต่ข้อนี้ใช้ "รหัส" ซึ่งชนกับ "รหัสไปรษณีย์" และ "รหัสข้อผิดพลาด" |
| 7 | This postcode does not match the selected subdistrict in the app's data. You can still use it. | รหัสนี้ไม่ตรงกับตำบลที่เลือก... ยังใช้รหัสนี้ได้ | รหัสไปรษณีย์นี้ไม่ตรงกับตำบลที่เลือก... แต่ยังใช้ได้ | ใช้ "รหัส" แทน "รหัสไปรษณีย์" ในข้อความเดียวกับที่ key ใช้ "postcode" |
| 8 | This postcode or subdistrict is not in the app's postal data. You can still use this postcode. | ไม่พบรหัสหรือตำบลนี้ในข้อมูลรหัสไปรษณีย์ของโปรแกรม ยังใช้รหัสนี้ได้ | ไม่พบรหัสไปรษณีย์หรือตำบลนี้ในข้อมูลของโปรแกรม แต่ยังใช้รหัสไปรษณีย์นี้ได้ | เหตุผลเดียวกับข้อ 7 |
| 9 | Enter a valid paper width and height. | กรุณาระบุความกว้างและความสูงที่ถูกต้องและไม่เกินพื้นที่สแกน | กรุณาระบุความกว้างและความสูงของกระดาษให้ถูกต้อง | ไทยเพิ่มเงื่อนไข "ไม่เกินพื้นที่สแกน" ที่ต้นฉบับไม่มี (ถ้าตั้งใจ ให้เพิ่มใน key ด้วย) |
| 10 | Digital ID / Digital IDs ... (24 key) | ปะปน "Digital ID" (24 ที่) กับ "ดิจิทัลไอดี" (15 ที่) | เลือกคำเดียว (แนะนำคง "Digital ID" ตามชื่อที่แสดงใน UI) | ศัพท์แนวคิดเดียวกันใช้สองรูปแบบ |
| 11 | Redaction permanently removes ... (ข้อความยาวของกล่องปิดทับข้อมูล) | ... ค่า... ที่ได้รับผลจากการปิดทับจะถูกล้าง | ใช้ "การลบข้อมูลถาวร" ให้เหมือนต้นข้อความ | ในย่อหน้าเดียวกันใช้ทั้ง "การลบข้อมูลถาวร" และ "การปิดทับ" (มี 2 key) |
| 12 | Also lock the document with my Digital ID after signing (proves who signed, shows later changes) | ... และตรวจพบการแก้ไขภายหลัง | ... และแสดงการแก้ไขภายหลัง | "shows later changes" ไม่ใช่ "ตรวจพบ" ความหมายเพี้ยนเล็กน้อย |
| 13 | The scanner is busy. Scanned pages have been kept. (YRK-SCN-002/005/006) | - | The scanned pages have been kept. / Scanning was cancelled. | อังกฤษ: ขาด article และกริยาในประโยคสั้น (แก้ได้ต่อเมื่อเปลี่ยน key + th.json พร้อมกัน) |
| 14 | An update was found but it is not signed by YRK, so it was deleted and not installed. (YRK-UPD-002) | - | An update was found, but it was not signed by YRK. It was deleted and not installed. | อังกฤษ: "it" กำกวมและขาดจุลภาคหน้า but |
| 15 | YRK PDF could not restart. (YRK-APP-004) | - | YRK PDF could not be restarted. | อังกฤษ: ไวยากรณ์กริยา |
| 16 | None of the first files could be opened for the preview. (YRK-BAT-003) | - | None of the selected files could be opened for preview. | อังกฤษ: "first files" ไม่ชัดเจน |
| 17 | Choose at least one file, language and output format. (YRK-CNV-001) | - | ... file, language, and output format. | วรรคตอนไม่สม่ำเสมอ (ส่วนอื่นของแคตตาล็อกใช้ Oxford comma หรือไม่ ควรเลือกแบบเดียว) |

หมายเหตุ
- ข้อ 4, 5, 13-17 แก้ที่ English key จึงต้องแก้ตำแหน่งที่ใช้ key ในโค้ด/XAML และ th.json พร้อมกัน
- ไม่พบ "ลายเซ็นต์" (สะกดผิด) ใน th.json
