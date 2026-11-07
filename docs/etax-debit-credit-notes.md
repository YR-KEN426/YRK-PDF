# e-Tax: ใบเพิ่มหนี้ / ใบลดหนี้ (80/81) และส่วนลด

สรุปจากแหล่งต้นทาง (Claude อ่าน 2026-11-07) ใช้ประกอบขั้น 2–4 ของ `plan.md` แผน 1.2.0

**แหล่ง**
- `XMLSchemaV2.zip` จาก etax.rd.go.th → สนับสนุน → ดาวน์โหลดโปรแกรม → "ไฟล์ตรวจสอบโครงสร้างข้อมูล (Schema & Schematron) v2.0" (URL ไฟล์: `https://etax.rd.go.th/etax_staticpage/app/download/XMLSchemaV2.zip`, ไฟล์ zip ลงวันที่ 2017-07-05) ไม่เก็บใน repo
- ETDA Rec. 3-2560 v2.0 ฉบับภาษาอังกฤษ (2025-05-15) จาก etda.or.th; ฉบับภาษาไทยเป็นฉบับหลัก
- กฎ: XSD และ Schematron ชนะข้อความในสเปกเสมอ (ดู PROJECT_NOTES)

## 1. ใบเพิ่มหนี้/ใบลดหนี้ใช้ root คนละตัวกับใบกำกับภาษี

| | ใบกำกับภาษี (388, T02–T04) | ใบเพิ่มหนี้/ใบลดหนี้ (80, 81) |
|---|---|---|
| root | `rsm:TaxInvoice_CrossIndustryInvoice` | `rsm:DebitCreditNote_CrossIndustryInvoice` |
| `rsm` | `urn:etda:uncefact:data:standard:TaxInvoice_CrossIndustryInvoice:2` | `urn:etda:uncefact:data:standard:DebitCreditNote_CrossIndustryInvoice:2` |
| `ram` | `...:TaxInvoice_ReusableAggregateBusinessInformationEntity:2` | `...:DebitCreditNote_ReusableAggregateBusinessInformationEntity:2` |
| XSD/SCH | `TaxInvoice_*_2p0` | `DebitCreditNote_*_2p0` |

โครงส่วนอื่น (คู่ค้า ที่อยู่ ภาษี รายการ) เหมือนใบกำกับ จึงใช้ตัวเขียนเดิมได้ แค่เปลี่ยน namespace ของ root และเพิ่มช่องตามข้อ 2–4
ข้อต่างเล็กที่ไม่กระทบเรา: ใน DCN `GrossPriceProductTradePrice` ใช้ `AppliedCITradeAllowanceCharge` (ใบกำกับใช้ `AppliedTradeAllowanceCharge`), และ `NetIncludingTaxesLineTotalAmount` ของบรรทัดไม่บังคับ — เราไม่ใช้ส่วนลดต่อหน่วย และยังเขียน NetIncluding ตามเดิมได้

## 2. `ExchangedDocument` (ลำดับตาม XSD)

`ID`, `Name`, `TypeCode`, `IssueDateTime`, `Purpose`?, **`PurposeCode` (บังคับใน DCN)**, `GlobalID`?, `CreationDateTime`, `IncludedNote`*

Schematron (`DCN-Document-001…007`):
- `TypeCode` = `80` คู่กับ `Name` = `ใบเพิ่มหนี้` ตรงตัว, `81` คู่กับ `ใบลดหนี้`
- `PurposeCode` ต้องมีเสมอ และต้องอยู่ในรายการด้านล่างตามประเภท
- รหัสลงท้าย `99` ต้องมี `Purpose` (ข้อความเหตุผล) ไม่ว่าง; รหัสอื่น Purpose ใส่ได้ (แนะนำใส่ข้อความของรหัสนั้นเสมอ เพื่อให้คนอ่าน XML เข้าใจ)

**รหัสเหตุผล** (สเปกภาคผนวก B.3, คำไทยแปลจากฉบับอังกฤษ ต้องเทียบฉบับไทยก่อนใส่ในโปรแกรม)

ใบเพิ่มหนี้ (80)
| รหัส | สินค้า/บริการ | เหตุผล |
|---|---|---|
| DBNG01 | สินค้า | ราคาสินค้าเพิ่ม (สินค้าเกินจำนวนที่ตกลง) |
| DBNG02 | สินค้า | คำนวณราคาสินค้าผิดพลาดต่ำกว่าที่เป็นจริง |
| DBNG99 | สินค้า | เหตุอื่น (ระบุ) |
| DBNS01 | บริการ | ค่าบริการเพิ่ม (บริการเกินข้อกำหนดที่ตกลง) |
| DBNS02 | บริการ | คำนวณค่าบริการผิดพลาดต่ำกว่าที่เป็นจริง |
| DBNS99 | บริการ | เหตุอื่น (ระบุ) |

ใบลดหนี้ (81)
| รหัส | สินค้า/บริการ | เหตุผล |
|---|---|---|
| CDNG01 | สินค้า | ลดราคาสินค้าที่ขาย (สินค้าผิดข้อกำหนดที่ตกลง) |
| CDNG02 | สินค้า | สินค้าชำรุดเสียหาย |
| CDNG03 | สินค้า | สินค้าขาดจำนวนตามที่ตกลง |
| CDNG04 | สินค้า | คำนวณราคาสินค้าผิดพลาดสูงกว่าที่เป็นจริง |
| CDNG05 | สินค้า | รับคืนสินค้า (ไม่ตรงตามคำพรรณนา) |
| CDNG99 | สินค้า | เหตุอื่น (ระบุ) |
| CDNS01 | บริการ | ลดค่าบริการ (บริการผิดข้อกำหนดที่ตกลง) |
| CDNS02 | บริการ | ค่าบริการขาดจำนวน |
| CDNS03 | บริการ | คำนวณค่าบริการผิดพลาดสูงกว่าที่เป็นจริง |
| CDNS04 | บริการ | บอกเลิกสัญญาบริการ |
| CDNS99 | บริการ | เหตุอื่น (ระบุ) |

## 3. อ้างใบกำกับเดิม: `ApplicableHeaderTradeAgreement/AdditionalReferencedDocument`

ลำดับใน `HeaderTradeAgreement`: `SellerTradeParty`, `BuyerTradeParty`, `ApplicableTradeDeliveryTerms`?, `BuyerOrderReferencedDocument`?, `AdditionalReferencedDocument`*

```xml
<ram:AdditionalReferencedDocument>
  <ram:IssuerAssignedID>INV-0012</ram:IssuerAssignedID>
  <ram:IssueDateTime>2026-11-01T00:00:00</ram:IssueDateTime>
  <ram:ReferenceTypeCode>388</ram:ReferenceTypeCode>
</ram:AdditionalReferencedDocument>
```
- สเปกตาราง 2 (3.1.5) ให้ทั้งสามช่องเป็น R สำหรับ DCN
- Schematron `DCN-AdditionalReferencedDocument-001`: ต้องมีอย่างน้อย 1 รายการที่ `ReferenceTypeCode` เป็น `388`, `T02`, `T03` หรือ `T04` → **ฟอร์มต้องให้เลือกประเภทของใบเดิมด้วย** (ค่าเริ่มต้น = ประเภทใบกำกับที่ใช้ล่าสุด)
- รอบนี้อ้างได้ใบเดียว (XSD รับหลายใบ ไว้ทีหลัง)

## 4. ยอดท้ายเอกสาร

ใบเพิ่ม/ลดหนี้ ใช้ `TradeSettlementMonetaryHeaderSummationType` ลำดับ:
`OriginalInformationAmount`, `LineTotalAmount`, `DifferenceInformationAmount`, `AllowanceTotalAmount`?, `ChargeTotalAmount`?, `TaxBasisTotalAmount`, `TaxTotalAmount`, `GrandTotalAmount` (นอกจากที่มี ? บังคับ)

ใบกำกับภาษี: `LineTotalAmount`, `AllowanceTotalAmount`?, `ChargeTotalAmount`?, `TaxBasisTotalAmount`, `TaxTotalAmount`, `GrandTotalAmount`

**ความหมาย (สเปกข้อ 7 กฎ R1–R3) และสิ่งที่เราจะเขียน**
- `OriginalInformationAmount` = มูลค่าตามใบกำกับเดิม (ก่อน VAT)
- `LineTotalAmount` = มูลค่าที่ถูกต้อง
- `DifferenceInformationAmount` = ผลต่าง; R3 เขียนว่า Original − LineTotal ซึ่งติดลบเมื่อเป็นใบเพิ่มหนี้ → **เราเขียนเป็นค่าบวกเสมอ** (|มูลค่าที่ถูกต้อง − มูลค่าเดิม|) ตามที่ใบเพิ่ม/ลดหนี้กระดาษแสดง และให้ Ken ตรวจด้วย validateschema
- `TaxBasisTotalAmount` = ผลต่าง (VAT คิดจากผลต่าง ตาม ม.86/9, 86/10) และ `ApplicableTradeTax/BasisAmount` = ผลต่าง
- `GrandTotalAmount` = ผลต่าง + VAT (R2)
- R1 (LineTotal = ผลรวม BasisAmount) ขัดกับ R2/R3 ถ้า BasisAmount คือผลต่าง → ยึด R2/R3 + Schematron; Schematron ของ DCN **ไม่ตรวจตัวเลขยอด** มีแค่ตรวจรหัสและช่องที่ต้องมี
- **บรรทัดรายการ** ในใบเพิ่ม/ลดหนี้ = รายการของผลต่าง (เช่น คืนสินค้า 2 ชิ้น) ผลรวมบรรทัด (หลังส่วนลด) ต้องเท่ากับผลต่าง; มูลค่าเดิมผู้ใช้กรอกเอง และมูลค่าที่ถูกต้องคำนวณ = เดิม − ผลต่าง (81) หรือ เดิม + ผลต่าง (80)

## 5. ส่วนลด: `SpecifiedTradeAllowanceCharge`

โครง (`TradeAllowanceChargeType`): `ChargeIndicator`?, `ActualAmount`?*, `ReasonCode`?, `Reason`?, `TypeCode`?
- ส่วนลด: `ChargeIndicator` = `false` (xsd:boolean), `ActualAmount currencyID="THB"` = จำนวนเงิน, `TypeCode` = `95` (UNECE 5189 Discount) ตามตัวอย่างสเปกข้อ 8.16; ไม่ใส่ ReasonCode/Reason
- ส่วนลด % ให้แปลงเป็นเงินก่อน (XML ไม่มีช่อง %)
- **ระดับบรรทัด** (สเปก 3.4.5.2): อยู่ใน `SpecifiedLineTradeSettlement` ลำดับ `ApplicableTradeTax`, `SpecifiedTradeAllowanceCharge`, `SpecifiedTradeSettlementLineMonetarySummation`; `NetLineTotalAmount` = ยอดหลังลด
- **ท้ายบิล** (สเปก 3.3.3): อยู่ใน `ApplicableHeaderTradeSettlement` ลำดับ `InvoiceCurrencyCode`, `ApplicableTradeTax`, `SpecifiedTradeAllowanceCharge`, … , `SpecifiedTradeSettlementHeaderMonetarySummation`
- `AllowanceTotalAmount` = ผลรวม ActualAmount ของ **ท้ายบิล** (สเปก 8.16: ผลรวมของ 3.3.3 ที่เป็น false ไปอยู่ 3.3.5.4) — ส่วนลดรายบรรทัดอยู่ใน NetLineTotalAmount แล้ว ไม่นับซ้ำ
- ค่าระดับบรรทัด (BasisAmount, NetLineTotalAmount, VAT ของบรรทัด) เป็นค่าก่อนส่วนลดท้ายบิล; ส่วนลดท้ายบิลมีผลเฉพาะ TaxBasis/TaxTotal/GrandTotal ที่หัวบิล จึงผลรวม VAT รายบรรทัดอาจไม่เท่า VAT หัวบิลเมื่อมีส่วนลดท้ายบิล
- `LineTotalAmount` (ใบกำกับ) = ผลรวม NetLineTotalAmount; `TaxBasisTotalAmount` = LineTotal − AllowanceTotal
- ไม่มีส่วนลดท้ายบิล → ไม่เขียน `SpecifiedTradeAllowanceCharge` และ `AllowanceTotalAmount` เลย (ให้ XML เหมือนเดิมทุก byte)
- ส่วนลดเป็น element ที่ไม่บังคับ (BD) ใน Schematron ไม่มีกฎตรวจ

## 6. ยังไม่ยืนยัน (ตรวจตอนขั้น 10)
- XMP `DocumentType` ของ PDF/A-3: ตอนนี้ฝัง "Tax Invoice"; จะใช้ "Debit Note" / "Credit Note" ตาม Name ภาษาอังกฤษของเอกสาร — สเปกฉบับนี้ไม่มีตาราง XMP ให้เทียบ
- ผลต่างเป็นค่าบวก (ข้อ 4) — validateschema ของกรมสรรพากรตรวจเฉพาะ Schema + Schematron จึงอาจไม่บอก; ถ้ามีเอกสารตัวอย่างของกรมฯ ให้เทียบ
- คำไทยของรหัสเหตุผล ต้องเทียบกับ ETDA ฉบับภาษาไทย
