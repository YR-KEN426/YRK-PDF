# เครื่องมือทดสอบเฉพาะกิจ

ใช้ .NET 10 SDK บน Windows รันจากราก repo ไม่ได้รวมใน solution, installer หรือชุดเทสต์ปกติ
Build ทีละโปรเจกต์ เพราะอ้าง Core ร่วมกัน (`bin/` และ `obj/` ถูก ignore จากราก repo อยู่แล้ว)

```powershell
dotnet build tools/WindowRenderer/WindowRenderer.csproj
dotnet build tools/LtvTestSigner/LtvTestSigner.csproj
dotnet build YrkPdfStudio.slnx

dotnet run --project tools/WindowRenderer --no-build -- SupportDialog th "$env:TEMP\yrk-support-th.png"
dotnet run --project tools/WindowRenderer --no-build -- SupportDialog en "$env:TEMP\yrk-support-en.png"
dotnet run --project tools/LtvTestSigner --no-build -- --offline
```

## WindowRenderer

รับชื่อคลาส Window ใน `App.Views`, `th`/`en` และพาธ PNG ใหม่ (ไม่เขียนทับไฟล์เดิม)
สร้าง WPF Application โหลด App resource dictionary/pack resources โดยไม่เรียก App startup/updater
แสดงหน้าต่างนอกจอ จัด layout แล้วบันทึก PNG; สีที่มองเห็นน้อยกว่า 8 สีถือว่าว่างและคืน exit code 1
รองรับหน้าต่างที่มี constructor ไม่รับพารามิเตอร์ เช่น `SupportDialog`
รองรับ `TrustCertificateDialog` ด้วยใบรับรองตัวอย่างในหน่วยความจำ ไม่อ่านใบรับรองผู้ใช้ ไม่บันทึก private key หรือเพิ่มลง certificate store
หน้าต่างอื่นที่ต้องรับ model/store จะรายงานข้อผิดพลาด ไม่สร้างข้อมูลผู้ใช้จริงเพื่อเติมพารามิเตอร์
App รุ่นนี้ไม่มีคลาส `CertificateDialog` (มี `TrustCertificateDialog` และ `TrustedCertificatesDialog` ซึ่งต้องรับพารามิเตอร์)

App และ services ใช้ `Environment.GetFolderPath(LocalApplicationData)` โดยตรง บน Windows การตั้ง
`LOCALAPPDATA` ของ process ไม่ได้เปลี่ยน known folder นี้ จึงเลิกเบี่ยง environment และใช้ **snapshot guard + allowlist**:
- ก่อนโหลด App ถ่าย snapshot แบบอ่านอย่างเดียวของโฟลเดอร์จริง `%LOCALAPPDATA%\YRK\EasyPdf`
  โดยใช้พาธจาก `GetFolderPath`: เก็บพาธสัมพัทธ์ ขนาด และ `LastWriteTimeUtc` ของทุกไฟล์รวมโฟลเดอร์ย่อย
  ถ้าโฟลเดอร์ไม่มีให้เก็บ snapshot ว่าง พร้อมสถานะการมีอยู่และ `CreationTimeUtc` ของโฟลเดอร์เพื่อตรวจการสร้างใหม่
- หลังเรนเดอร์และปิดหน้าต่าง รวมกรณี exception ตรวจ snapshot อีกครั้ง หากไฟล์เพิ่ม/ลด/เปลี่ยน
  หรือโฟลเดอร์ถูกสร้าง/ลบ/สร้างใหม่ พิมพ์ `FAIL: data folder changed` พร้อมรายการที่ต่าง และคืน **exit code 4**
  ถ้าตรวจ snapshot หลังรันไม่ได้ก็คืน 4; `PASS` จะแสดงหลัง guard ผ่านเท่านั้น
- เครื่องมือไม่สร้างหรือเขียนในโฟลเดอร์ข้อมูล ไม่ลบหรือคืนข้อมูลอัตโนมัติ และปฏิเสธพาธ PNG ภายในโฟลเดอร์นี้
  guard ตรวจพบการเปลี่ยนแปลงหลังเกิดขึ้น ไม่ได้ป้องกันการเขียน และไม่ลบ PNG ที่สร้างไปแล้วเมื่อ guard ไม่ผ่าน
- allowlist ที่ตรวจ constructor/XAML แล้ว: `SupportDialog` สร้าง UI เท่านั้น;
  `TrustCertificateDialog` อ่านรายละเอียดใบรับรอง;
  `TrustedCertificatesDialog` เรียก `TrustedCertificateStore.LoadAll()` ซึ่งอ่านไฟล์โดยไม่สร้างโฟลเดอร์
  การเพิ่ม/ลบใบรับรองอยู่ในปุ่มที่เครื่องมือไม่ได้กด `TrustedCertificatesDialog` ยังรันไม่ได้เพราะต้องรับ store
- หน้าต่างนอก allowlist ต้องระบุ `--allow-any-window` อย่างชัดเจน เช่น
  `dotnet run --project tools/WindowRenderer --no-build -- --allow-any-window SomeDialog en "$env:TEMP\yrk-other.png"`
  flag นี้ไม่ข้าม snapshot guard หรือข้อกำหนด constructor ว่าง (ยกเว้น fixture ของ `TrustCertificateDialog`)
  ชื่อคลาสใช้แบบสั้นหรือชื่อเต็มใน `Yrk.PdfStudio.App.Views` ได้ ควรทบทวน allowlist เมื่อโค้ด App เปลี่ยน

```powershell
dotnet run --project tools/WindowRenderer --no-build -- TrustCertificateDialog th "$env:TEMP\yrk-certificate-th.png"
```

Snapshot ตรวจ metadata ก่อน/หลัง จึงตรวจไม่พบการเปลี่ยนแล้วคืน metadata เดิมทั้งหมดระหว่างรัน

อาร์กิวเมนต์ผิดหรือหน้าต่างนอก allowlist ที่ไม่ระบุ flag คืน 2; เรนเดอร์ผิดพลาดคืน 1;
สำเร็จและข้อมูลไม่เปลี่ยนคืน 0 ไม่มีการแก้ known folder/registry หรือสำรองคืนข้อมูลจริง

## LtvTestSigner

ไม่ใส่อาร์กิวเมนต์เท่ากับ `--offline`: สร้าง CA, ใบปลายทางพร้อม private key และ CRL ในหน่วยความจำ
สร้าง PDF ตัวอย่างเองลง temp เซ็นผ่าน `PdfSigner` พร้อม `RevocationSource` และตรวจผ่าน `PdfSignatureVerifier`
ตามแนว `SignatureLtvTests` (ใบ CA อยู่ใน DSS และใช้ trust เฉพาะการตรวจครั้งนั้นผ่าน internal option ด้วย reflection)
ตรวจ signature intact, whole document, embedded revocation, identity และ status พร้อม negative control เมื่อไม่ให้ trust CA
พิมพ์ `PASS`/`FAIL` และคืน 0/1; อาร์กิวเมนต์ผิดคืน 2 ลบ temp หลังรัน ไม่ส่งออก private key
ไม่อ่าน Digital ID จริง ไม่เพิ่มใบรับรองลง Windows store และไม่ใช้เครือข่ายในโหมด offline
โหมด offline ตรวจ LTV/CRL โดยไม่มี timestamp และไม่ได้ยืนยันอายุระยะยาวจาก TSA จริง

ใช้ timestamp จริงผ่าน `HttpTimestampAuthority` ของ Core ได้เมื่อระบุ URL เอง:

```powershell
dotnet run --project tools/LtvTestSigner --no-build -- --timestamp https://YOUR-TSA-ENDPOINT
```

โหมดนี้เชื่อมต่อ TSA และ CRL ของ TSA ตามที่ Core รองรับ (มี timeout); ความล้มเหลวหรือ timestamp ตรวจไม่ผ่านจะคืน FAIL
ไม่ได้ทดสอบโหมดเครือข่ายในการตรวจครั้งนี้ ผล offline ที่ลองจริงเป็น `Valid`, `Trusted`, `RevocationChecked=True`
ทั้งสองโปรเจกต์และ solution build ผ่าน; อาจมี NU1900 หาก NuGet vulnerability service เข้าไม่ได้
