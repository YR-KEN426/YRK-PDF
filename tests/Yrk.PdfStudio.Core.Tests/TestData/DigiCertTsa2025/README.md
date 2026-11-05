# DigiCert timestamp CA (2025 CA1): real CRL test data

Public data, downloaded 2026-11-05 (Asia/Bangkok). Used to test that `CrlValidation` accepts a real CRL whose
Issuing Distribution Point extension is critical (the cause of failed LTV signing in 1.1.5).

| File | Source | SHA-256 |
|---|---|---|
| `ca1.crl` | http://crl3.digicert.com/DigiCertTrustedG4TimeStampingRSA4096SHA2562025CA1.crl | `94d2d1010e606a25c82755099cc36b52fd79e24fe676bbc61ecef01f144c2005` |
| `ca1.cer` | http://cacerts.digicert.com/DigiCertTrustedG4TimeStampingRSA4096SHA2562025CA1.crt (DER) | `ca0b1554ecd901ea19dcad8749e9f2648c8d6dfcea1add9d2c2109415bb82ccd` |
| `tsa-leaf.cer` | Signer of a timestamp from http://timestamp.digicert.com for a hash of random bytes (DER) | `2da09da7f4131f9fe72db6c5e6e9c9656755af043f1ea742cc0d2120e141ebfc` |

- `ca1.crl`: issuer `DigiCert Trusted G4 TimeStamping RSA4096 SHA256 2025 CA1`, CRL number 80,
  thisUpdate 2026-11-04 16:40:26 UTC, nextUpdate 2026-11-25 16:40:26 UTC, no revoked certificates.
  Critical Issuing Distribution Point with fullName crl3 and crl4 URIs only. Signature verifies with `ca1.cer`.
- `tsa-leaf.cer`: `DigiCert SHA256 RSA4096 Timestamp Responder 2026 1`, serial `084FDC334F7E454EDBC30F8FF9921835`,
  CA:FALSE, CRL Distribution Point `http://crl3.digicert.com/DigiCertTrustedG4TimeStampingRSA4096SHA2562025CA1.crl`.
- Tests must check at a fixed time inside the CRL's validity, for example 2026-11-10 00:00 UTC, never at "now".
