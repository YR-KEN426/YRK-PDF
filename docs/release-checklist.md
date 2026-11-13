# เช็กลิสต์ก่อนปล่อยรุ่น (สำหรับ Ken)

## ทดสอบการอัปเดตก่อนปล่อย

1. **ตั้งเลขรุ่น** ใน PowerShell หน้าต่างเดียวที่ใช้ตลอด (ทุกคำสั่งในรายการใช้ตัวแปรนี้ ห้ามพิมพ์เลขรุ่นเอง):
   ```powershell
   $Ver = "x.y.z"      # รุ่นที่จะปล่อย
   $PrevVer = "x.y.z"  # รุ่นก่อนหน้าที่ติดตั้งอยู่
   ```
2. build และเซ็น **feed ทดสอบรุ่น $Ver** โดย**ไม่ใส่ `-Publish`** ขั้นนี้ Ken ทำเอง เพราะต้องใช้กุญแจ:
   ```powershell
   cd "D:\Software Project\YRK-PDF"
   powershell -ExecutionPolicy Bypass -File build\release.ps1 -GitHubRepo YR-KEN426/yrk-easy-pdf-release -ReleaseNotes docs\release-notes\$Ver.md
   ```
   - แล้วสร้างโฟลเดอร์ทดสอบแยก เช่น `C:\yrk-test\feed` คัดลอกจาก `artifacts\releases\win-x64` มาแค่ `YRK.EasyPdf-$Ver-full.nupkg`, `YRK.EasyPdf-$Ver.sig` และไฟล์ feed (`releases.win.json`, `assets.win.json`, `RELEASES` ถ้ามี) เพราะสคริปต์ในข้อถัดไปต้องการ `-full.nupkg` ไฟล์เดียว
   - แพ็กเกจนี้เซ็นด้วยกุญแจจริง ห้ามส่งให้ใครหรืออัปโหลด
3. ที่โฟลเดอร์โปรเจกต์ รัน (สคริปต์นี้ไม่ต้องใช้กุญแจ):
   ```powershell
   cd "D:\Software Project\YRK-PDF"
   powershell -File build\make-tampered-feed.ps1 -Feed "C:\yrk-test\feed" -FixFeedHash
   ```
   - จะได้โฟลเดอร์ใหม่ 2 อัน ข้างโฟลเดอร์ feed: `-tampered-updateexe` (แก้ `Update.exe` ในแพ็กเกจ) และ `-tampered-sig` (แก้ลายเซ็น 1 byte)
   - ควรใส่ `-FixFeedHash` เพื่อให้ hash ใน feed ตรงกับแพ็กเกจที่แก้ ไม่อย่างนั้น Velopack อาจปฏิเสธเองก่อนถึงการตรวจลายเซ็นของเรา แล้วเราจะไม่รู้ว่าการตรวจของเราทำงานจริงหรือไม่
   - สคริปต์ไม่แก้โฟลเดอร์ต้นทาง และตรวจ hash ของทุกไฟล์ก่อนและหลังให้เอง ถ้ามีโฟลเดอร์ผลลัพธ์อยู่แล้วต้องใส่ `-Force`
4. ติดตั้ง **$PrevVer** ให้เรียบร้อย หาตำแหน่ง `Update.exe` ที่ติดตั้งจริง (เครื่อง Ken อยู่ที่ `%LocalAppData%\YRK.EasyPdf\Update.exe`) แล้วจด hash ไว้:
   ```powershell
   Get-FileHash "$env:LOCALAPPDATA\YRK.EasyPdf\Update.exe" -Algorithm SHA256
   ```
5. ทดสอบทีละโฟลเดอร์ ให้เริ่มจาก $PrevVer ที่ติดตั้งใหม่ทุกครั้ง และปิดโปรแกรมระหว่างแต่ละรอบ ลำดับคือ `-tampered-updateexe` แล้ว `-tampered-sig` แล้วจึงทดสอบ feed ที่ถูกต้องเป็นรอบสุดท้าย
   - ใน PowerShell หน้าต่างเดียวกัน ให้ตั้ง `$env:YRK_PDFSTUDIO_UPDATE_URL = '<path เต็มของโฟลเดอร์>'` แล้วเปิดโปรแกรมที่ติดตั้งไว้จากหน้าต่างนั้น
   - ไปที่ **เกี่ยวกับ YRK PDF → ตรวจหาการอัปเดตตอนนี้**
6. ผลที่ต้องได้:
   - **แพ็กเกจที่ถูกแก้ทั้ง 2 แบบ:** ขึ้นข้อผิดพลาด ไม่มีปุ่มรีสตาร์ท และรุ่นยังเป็น $PrevVer จากนั้นรัน `Get-FileHash` กับ `Update.exe` อีกครั้ง ค่าต้องเท่ากับที่จดไว้ และในโฟลเดอร์ packages ต้องไม่มีแพ็กเกจที่ถูกปฏิเสธค้างอยู่ (ดูทั้งโฟลเดอร์ `packages` ใต้โฟลเดอร์ติดตั้ง)
   - **feed ที่ถูกต้อง:** มีอัปเดตให้ ขึ้นปุ่มรีสตาร์ท กดแล้วได้ $Ver
   - ทำรอบ feed ที่ถูกต้อง 2 ครั้ง (เริ่มจาก $PrevVer ใหม่ทุกครั้ง): ครั้งแรกกดรีสตาร์ทจาก**หน้า Home** ครั้งที่สองเปิด PDF แล้วกดจาก**แท็บเอกสาร**
   - ระหว่างดาวน์โหลด (ของใหม่ใน 1.1.8 เห็นครั้งแรกในรอบนี้): แถบขึ้น % และ MB ทั้งบน Home และในแท็บ; ถ้าสะดวกตัดเน็ตกลางทาง ครบ 30 วินาทีต้องมีข้อความเตือน และกดยกเลิกแล้วแถบไม่ค้าง
7. **ตรวจในตัวที่ติดตั้งจริง** ($Ver ที่ได้จากข้อ 6 ซึ่งเป็น Release build เดียวกับที่จะปล่อย):
   - เปิด**ทุกหน้าต่างที่ใหม่หรือถูกแก้ในรุ่นนี้** ทั้งภาษาไทยและอังกฤษ ดูว่าเปิดได้และข้อความครบ (1.1.5 หน้าต่างนโยบายเปิดไม่ได้ ทั้งที่เทสต์ผ่านครบ)
     - รุ่น $Ver: หน้าต่าง e-Tax (เลือกใบลดหนี้/ใบเพิ่มหนี้ ดูกล่องใบกำกับภาษีเดิม ช่องส่วนลด), "ใบรับรองที่ฉันเชื่อถือ" ตอนรายการว่าง, หน้าต่างสนับสนุนผู้พัฒนา (ปุ่ม Ko-fi + GitHub Sponsors), ช่วยเหลือ → คู่มือ ต้องเป็นคู่มือ $Ver (มีหัวข้อใบเพิ่มหนี้/ลดหนี้)
     - เปิดไฟล์ไปหน้า 30 ปิด เปิดใหม่ต้องกลับมาหน้า 30
   - **เซ็น LTV จริง 1 ครั้ง** Claude ทำแทน Ken (Ken ตกลง 2026-11-09): ใช้ใบรับรองทดสอบชั่วคราว + ตราประทับเวลาจริงของ DigiCert ตอนเปิดเน็ต แล้วตรวจลายเซ็นว่าผ่าน (1.1.6 พบว่า CA เปลี่ยนรูปแบบ CRL ได้โดยไม่บอก) วิธีนี้ไม่ได้ทดสอบ CRL ของ CA ที่ออก Digital ID จริง
   - **หลังข้อ 6 ห้ามแก้โค้ดอีก:** ถ้าต้องแก้ ให้ build feed ทดสอบใหม่และทดสอบซ้ำ หรืออย่างน้อยบอก Ken ให้ติดตั้ง `Setup.exe` ของ release ทับหลังปล่อย เพราะตัวที่ติดตั้งจาก feed ทดสอบมีเลขรุ่นเท่ากับตัวจริง จึงไม่ได้รับอัปเดต (บทเรียน $Ver)
8. เสร็จแล้ว:
   - รัน `Remove-Item Env:YRK_PDFSTUDIO_UPDATE_URL`
   - ถ้าเคยตั้งค่านี้แบบถาวรด้วย `setx` ให้ลบด้วย `[Environment]::SetEnvironmentVariable('YRK_PDFSTUDIO_UPDATE_URL', $null, 'User')`
   - ลบ `C:\yrk-test\feed` และโฟลเดอร์ tampered ทั้ง 2 อัน
9. **ปล่อยจริง (เมื่อทุกข้อผ่าน):**
   - รัน:
     ```powershell
     cd "D:\Software Project\YRK-PDF"
     powershell -ExecutionPolicy Bypass -File build\release.ps1 -GitHubRepo YR-KEN426/yrk-easy-pdf-release -Publish -ReleaseNotes docs\release-notes\$Ver.md
     ```
   - ตรวจลายเซ็นของแพ็กเกจที่ปล่อยแล้ว:
     ```powershell
     cd "D:\Software Project\YRK-PDF"
     dotnet run --project build\UpdateSigner -- verify --package artifacts\releases\win-x64\YRK.EasyPdf-$Ver-full.nupkg --id YRK.EasyPdf --version $Ver --sig artifacts\releases\win-x64\YRK.EasyPdf-$Ver.sig --content-sig artifacts\releases\win-x64\YRK.EasyPdf-$Ver.content.sig
     ```
   - แล้วทำต่อตามหัวข้อ "การออกรุ่น" ใน `PROJECT_NOTES.md`: `release.ps1 -Publish` สร้างหน้าเว็บให้เองแล้ว ไม่ต้องรัน `build-site.ps1` แยก อัปโหลดหน้าเว็บตามที่สคริปต์บอก, ตรวจออนไลน์ และบันทึกสิ่งที่เรียนรู้
