# เช็กลิสต์ก่อนปล่อยรุ่น (สำหรับ Ken)

## ทดสอบการอัปเดตก่อนปล่อย

1. **สำรองโฟลเดอร์ feed ก่อน:** คัดลอก `artifacts\releases\win-x64` ทั้งโฟลเดอร์ไปไว้ที่อื่น เช่น `artifacts\releases\win-x64-backup` เพราะ vpk ไม่ยอมแพ็กทับรุ่นที่มีอยู่แล้ว ถ้าไม่สำรองไว้ ตอนปล่อยจริงในข้อ 8 จะหยุดเอง
2. build และเซ็น **feed ทดสอบรุ่น 1.1.5** โดย**ไม่ใส่ `-Publish`** ขั้นนี้ Ken ทำเอง เพราะต้องใช้กุญแจ:
   ```powershell
   powershell -ExecutionPolicy Bypass -File build\release.ps1 -GitHubRepo YR-KEN426/yrk-easy-pdf-release -ReleaseNotes docs\release-notes\1.1.6.md
   ```
   - แล้วสร้างโฟลเดอร์ทดสอบแยก เช่น `C:\yrk-test\feed` คัดลอกจาก `artifacts\releases\win-x64` มาแค่ `YRK.EasyPdf-1.1.5-full.nupkg`, `YRK.EasyPdf-1.1.5.sig` และไฟล์ feed (`releases.win.json`, `assets.win.json`, `RELEASES` ถ้ามี) เพราะสคริปต์ในข้อถัดไปต้องการ `-full.nupkg` ไฟล์เดียว
   - แพ็กเกจนี้เซ็นด้วยกุญแจจริง ห้ามส่งให้ใครหรืออัปโหลด
3. ที่โฟลเดอร์โปรเจกต์ รัน (สคริปต์นี้ไม่ต้องใช้กุญแจ):
   ```powershell
   powershell -File build\make-tampered-feed.ps1 -Feed "C:\yrk-test\feed" -FixFeedHash
   ```
   - จะได้โฟลเดอร์ใหม่ 2 อัน ข้างโฟลเดอร์ feed: `-tampered-updateexe` (แก้ `Update.exe` ในแพ็กเกจ) และ `-tampered-sig` (แก้ลายเซ็น 1 byte)
   - ควรใส่ `-FixFeedHash` เพื่อให้ hash ใน feed ตรงกับแพ็กเกจที่แก้ ไม่อย่างนั้น Velopack อาจปฏิเสธเองก่อนถึงการตรวจลายเซ็นของเรา แล้วเราจะไม่รู้ว่าการตรวจของเราทำงานจริงหรือไม่
   - สคริปต์ไม่แก้โฟลเดอร์ต้นทาง และตรวจ hash ของทุกไฟล์ก่อนและหลังให้เอง ถ้ามีโฟลเดอร์ผลลัพธ์อยู่แล้วต้องใส่ `-Force`
4. ติดตั้ง **1.1.4** ให้เรียบร้อย หาตำแหน่ง `Update.exe` ที่ติดตั้งจริง (น่าจะอยู่ที่ `%LocalAppData%\YrkEasyPdf\Update.exe` แต่ให้ตรวจก่อน) แล้วจด hash ไว้:
   ```powershell
   Get-FileHash "$env:LOCALAPPDATA\YrkEasyPdf\Update.exe" -Algorithm SHA256
   ```
5. ทดสอบทีละโฟลเดอร์ ให้เริ่มจาก 1.1.4 ที่ติดตั้งใหม่ทุกครั้ง และปิดโปรแกรมระหว่างแต่ละรอบ ลำดับคือ `-tampered-updateexe` แล้ว `-tampered-sig` แล้วจึงทดสอบ feed ที่ถูกต้องเป็นรอบสุดท้าย
   - ใน PowerShell หน้าต่างเดียวกัน ให้ตั้ง `$env:YRK_PDFSTUDIO_UPDATE_URL = '<path เต็มของโฟลเดอร์>'` แล้วเปิดโปรแกรมที่ติดตั้งไว้จากหน้าต่างนั้น
   - ไปที่ **เกี่ยวกับ YRK PDF → ตรวจหาการอัปเดตตอนนี้**
6. ผลที่ต้องได้:
   - **แพ็กเกจที่ถูกแก้ทั้ง 2 แบบ:** ขึ้นข้อผิดพลาด ไม่มีปุ่มรีสตาร์ท และรุ่นยังเป็น 1.1.4 จากนั้นรัน `Get-FileHash` กับ `Update.exe` อีกครั้ง ค่าต้องเท่ากับที่จดไว้ และในโฟลเดอร์ packages ต้องไม่มีแพ็กเกจที่ถูกปฏิเสธค้างอยู่ (ดูทั้งโฟลเดอร์ `packages` ใต้โฟลเดอร์ติดตั้ง)
   - **feed ที่ถูกต้อง:** มีอัปเดตให้ ขึ้นปุ่มรีสตาร์ท กดแล้วได้ 1.1.5
7. เสร็จแล้ว:
   - รัน `Remove-Item Env:YRK_PDFSTUDIO_UPDATE_URL`
   - ถ้าเคยตั้งค่านี้แบบถาวรด้วย `setx` ให้ลบด้วย `[Environment]::SetEnvironmentVariable('YRK_PDFSTUDIO_UPDATE_URL', $null, 'User')`
   - ลบ `C:\yrk-test\feed` และโฟลเดอร์ tampered ทั้ง 2 อัน
8. **ปล่อยจริง (เมื่อทุกข้อผ่าน):**
   - ลบ `artifacts\releases\win-x64` ที่มีแพ็กเกจทดสอบ แล้วคืนโฟลเดอร์สำรองจากข้อ 1 เป็น `artifacts\releases\win-x64`
   - รัน:
     ```powershell
     powershell -ExecutionPolicy Bypass -File build\release.ps1 -GitHubRepo YR-KEN426/yrk-easy-pdf-release -Publish -ReleaseNotes docs\release-notes\1.1.6.md
     ```
   - แล้วทำต่อตามหัวข้อ "การออกรุ่น" ใน `PROJECT_NOTES.md`: รัน `build\build-site.ps1`, อัปโหลดหน้าเว็บ, ตรวจออนไลน์ และบันทึกสิ่งที่เรียนรู้
