# แผน: ย้ายหน้าเว็บไป `pdf.yrklab.com` (ไม่ออกรุ่นโปรแกรม)

## ที่มา
Ken ตกลง 2026-11-12 ให้ย้ายหน้าเว็บทันทีหลัง 1.2.1 เพื่อให้ README, ลิงก์ และข้อความหน้า Store ใช้โดเมนใหม่ครบก่อนลง Store (PROJECT_NOTES "รายการย้ายหน้าเว็บ")
- เปลี่ยนแค่ `yr-ken426.github.io/yrk-easy-pdf-release` → `pdf.yrklab.com` ลิงก์ Ko-fi และ GitHub Sponsors ไม่เปลี่ยน
- เมื่อตั้ง custom domain แล้ว GitHub จะ redirect ที่อยู่ github.io เดิมไปโดเมนใหม่เอง ลิงก์ในโปรแกรม 1.2.1 และคู่มือ PDF เดิมจึงยังใช้ได้
- การอัปเดตโปรแกรมไม่กระทบ เพราะ `AppUpdater` ใช้ `GithubSource` อ่าน release ของ repo ไม่ได้อ่านจาก GitHub Pages

## ขอบเขต
ทำ: ตั้งโดเมน, แก้ลิงก์ในหน้าเว็บ / README / SECURITY / FUNDING, build หน้าเว็บ, อัปโหลด, ตรวจออนไลน์
ไม่ทำ (ไป 1.2.2): `src/Yrk.PdfStudio.App/Services/Feedback.cs` (`WebsiteUrl`), `docs/guide/th.html` + `en.html` (ไฟล์ละ 3 จุด) และ PDF คู่มือ
ห้ามเปลี่ยน: ชื่อ repo `yrk-easy-pdf-release` ใน `build-site.ps1`, `download-stats.ps1`, `AppUpdater.cs` และ feed อัปเดต

## ขั้นตอน

### ขั้น 0: Ken ตั้งโดเมน (Ken ทำเอง เพราะเป็นการเปลี่ยนค่าบัญชี)
ต้องทำก่อนแก้ลิงก์ ไม่อย่างนั้น hreflang และลิงก์แชร์จะชี้ไปที่อยู่ที่ยังเปิดไม่ได้
1. Cloudflare → `yrklab.com` → DNS → เพิ่ม `CNAME` ชื่อ `pdf` ค่า `yr-ken426.github.io` **Proxy status: DNS only** (เมฆสีเทา)
2. GitHub → Settings ของบัญชี → Pages → Verified domains → Add `yrklab.com` แล้วเพิ่ม TXT record ที่ GitHub ให้มาใน Cloudflare จากนั้นกด Verify
   - กันคนอื่นเอาโดเมนย่อยของเราไปผูกกับ repo ของเขา
3. repo `yrk-easy-pdf-release` → Settings → Pages → Custom domain ใส่ `pdf.yrklab.com` → Save → รอ DNS check ผ่าน → ติ๊ก **Enforce HTTPS** (ใบรับรองอาจใช้เวลาถึงประมาณ 1 ชั่วโมง)
   - GitHub จะสร้างไฟล์ `docs/CNAME` ใน repo นั้น **ห้ามลบ** ตอนอัปโหลดหน้าเว็บครั้งต่อไป
- **Claude ตรวจ:** `https://pdf.yrklab.com/` และ `/en/` เปิดได้ด้วย HTTPS, ที่อยู่ github.io เดิม (รวม `/en/`) redirect 301 มาโดเมนใหม่, DNS ไม่ผ่าน proxy ของ Cloudflare

### ขั้น 1: แก้ลิงก์ในไฟล์ต้นฉบับ (implementer → Codex `gpt-6-luna` · low เพราะเป็นการแทนข้อความแบบกลไก)
แก้ได้เฉพาะไฟล์เหล่านี้ แทน `https://yr-ken426.github.io/yrk-easy-pdf-release/` ด้วย `https://pdf.yrklab.com/` (รวมส่วน `en/` ต่อท้าย) และข้อความที่แสดง `yr-ken426.github.io/yrk-easy-pdf-release` ด้วย `pdf.yrklab.com`
- `docs/release-repo/site-src/index.html` บรรทัด 9, 10, 441
- `docs/release-repo/site-src/en/index.html` บรรทัด 9, 10, 440
- `docs/release-repo/README.md` บรรทัด 18
- `README.md` บรรทัด 8, 491, 493
- `SECURITY.md` บรรทัด 23
- `.github/FUNDING.yml` เพิ่ม `ko_fi: yrklab` (คง `github: YR-KEN426`)
- สร้าง `docs/release-repo/.github/FUNDING.yml` เนื้อหาเดียวกัน ไว้ให้ Ken อัปโหลดขึ้น repo ปล่อยรุ่น
- ทดสอบ: ค้นคำว่า `github.io` ทั้ง repo (ยกเว้น `docs/release-repo/site/`, `artifacts/`, `Assets/Guide/`) ต้องเหลือแค่ `PROJECT_NOTES.md`, `docs/guide/*.html` และ `Feedback.cs` (ของ 1.2.2)

### ขั้น 2: build หน้าเว็บและตรวจ (Claude)
- รัน `build\build-site.ps1` (ตรวจนโยบายความเป็นส่วนตัวให้ด้วย) ต้องผ่านไม่มี error
- ค้นใน `site\index.html` และ `site\en\index.html` ว่ามี `pdf.yrklab.com` และไม่มี `github.io` เหลือ (นับเฉพาะ ไม่เปิดอ่านทั้งไฟล์)
- Claude ตรวจ `git diff` แล้ว commit **ห้าม push จนกว่า Ken จะยืนยัน**

### ขั้น 3: Ken อัปโหลดและแก้ลิงก์ภายนอก (Ken ทำเอง)
- repo `yrk-easy-pdf-release` (อัปโหลดผ่าน GitHub ตามเดิม **อย่าลบ `docs/CNAME`**)
  - `docs\release-repo\site\index.html` → `docs/`
  - `docs\release-repo\site\en\index.html` → `docs/en/`
  - `docs\release-repo\README.md` → ราก repo
  - `docs\release-repo\.github\FUNDING.yml` → `.github/`
- ช่อง Website ใน About ของ repo `YRK-PDF` และ `yrk-easy-pdf-release` → `https://pdf.yrklab.com`
- Ko-fi: แก้ลิงก์ในโพสต์ปักหมุด (หน้าไทย / อังกฤษ) และลิงก์เว็บในโปรไฟล์ถ้ามี
- GitHub Sponsors / โปรไฟล์ GitHub ถ้ามีลิงก์เว็บเดิม

### ขั้น 4: ตรวจออนไลน์และบันทึก (Claude)
- เทียบหน้าเว็บออนไลน์กับไฟล์ใน `site\` ทั้งสองภาษา, ปุ่มดาวน์โหลดได้ไฟล์จริง, ลิงก์แชร์และ hreflang ชี้โดเมนใหม่
- ที่อยู่เดิม `.../yrk-easy-pdf-release/#privacy` (ลิงก์ในคู่มือ PDF เดิม) redirect แล้วยังไปถึงส่วนนโยบายความเป็นส่วนตัว
- ปุ่ม Sponsor ของ repo ขึ้นทั้ง GitHub Sponsors และ Ko-fi
- อัปเดต PROJECT_NOTES: ติ๊กรายการย้ายหน้าเว็บว่าทำแล้ว คงรายการไฟล์ของ 1.2.2 ไว้

## การตรวจผล (ทั้งหมด)
- `https://pdf.yrklab.com/` และ `/en/` เปิดได้ด้วย HTTPS เนื้อหาตรงกับ build ล่าสุด
- ที่อยู่ github.io เดิมทุกแบบ redirect มาโดเมนใหม่
- ไฟล์ `docs/CNAME` ยังอยู่ใน repo ปล่อยรุ่น
- ไม่มีการแก้โค้ดโปรแกรมหรือ feed อัปเดต

## สถานะ
- [x] ขั้น 0 Ken ตั้งโดเมน + Claude ตรวจ (2026-11-13: CNAME DNS only + TXT ยืนยัน yrklab.com, custom domain + Enforce HTTPS; HTTPS 200, github.io เดิม 301 → pdf.yrklab.com; redirect บางที่อยู่ยังไป http:// รอ CDN แล้วตรวจซ้ำในขั้น 4)
- [x] ขั้น 1 แก้ลิงก์ในไฟล์ต้นฉบับ
- [ ] ขั้น 2 build หน้าเว็บ + ตรวจ + commit
- [ ] ขั้น 3 Ken อัปโหลด + แก้ลิงก์ภายนอก
- [ ] ขั้น 4 ตรวจออนไลน์ + อัปเดต PROJECT_NOTES
