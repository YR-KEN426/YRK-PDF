# Third-party notices

YRK PDF includes the open-source components listed below. The full copyright notices and license
texts are in the `licenses` folder, which ships with the program (installer and portable package) and is
shown in **? Help ▸ About YRK PDF**. YRK PDF's own code is under the Apache License 2.0 (`LICENSE`, `NOTICE`).

| Component (version shipped) | Used for | License | Full text |
|---|---|---|---|
| [PDFium](https://pdfium.googlesource.com/pdfium/) chromium/8100, built by [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) 157.0.8100 (`pdfium.dll`) | Opening, rendering and editing PDF | BSD-3-Clause and Apache-2.0 (PDFium); MIT (build scripts; the NuGet package declares Apache-2.0) | `licenses/pdfium/pdfium.txt`, `pdfium-binaries.txt` |
| Code inside `pdfium.dll`: Abseil, Anti-Grain Geometry 2.3, BigInt, Dragonbox, fast_float, FreeType, ICU, Little CMS, libc++, libjpeg-turbo, OpenJPEG, libpng, LLVM libc, simdutf, zlib | Parts of PDFium | Apache-2.0, Apache-2.0 WITH LLVM-exception, BSD, Boost, FreeType (FTL), IJG, Unicode, MIT, zlib and similar permissive licenses | `licenses/pdfium/*.txt` |
| [PDFsharp](https://github.com/empira/PDFsharp) **6.2.4** (`PdfSharp*.dll`) | Compress PDF | MIT | `licenses/pdfsharp.txt` |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.0 | MVVM helpers | MIT | `licenses/communitytoolkit-mvvm.txt`, `communitytoolkit-mvvm-notices.txt` |
| [Velopack](https://github.com/velopack/velopack) 1.2.161 (`Velopack.dll`, `Update.exe`, `Setup.exe`) | Installer, portable package and updates | MIT | `licenses/velopack.txt` |
| [ACadSharp](https://github.com/DomCR/ACadSharp) 3.8.0, with [CSUtilities and CSMath](https://github.com/DomCR/CSUtilities) built in | Reading AutoCAD drawings (.dwg, .dxf) | MIT | `licenses/acadsharp.txt`, `csutilities.txt` |
| [.NET](https://github.com/dotnet/runtime) 10.0.13 runtime and [WPF](https://github.com/dotnet/wpf) (self-contained) | Application runtime | MIT, with the third-party notices listed by Microsoft | `licenses/dotnet/*.txt` |
| System.Security.Cryptography.Pkcs 10.0.13, Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2, Microsoft.Extensions.Logging.Abstractions 8.0.3 | Digital signatures and timestamps; used by Velopack | MIT | `licenses/dotnet/LICENSE.txt`, `runtime-notices.txt`, `extensions-8.0-notices.txt` |
| [Tesseract for .NET](https://github.com/charlesw/tesseract) 5.2.0 (`Tesseract.dll`) with [Tesseract OCR](https://github.com/tesseract-ocr/tesseract) 5.0.0 (`x64/tesseract50.dll`) | Text recognition (OCR) | Apache-2.0 | `licenses/tesseract-apache-2.0.txt` |
| [Leptonica](http://www.leptonica.org/) 1.82.0 (`x64/leptonica-1.82.0.dll`), with libpng 1.6.37, zlib 1.2.13, libjpeg-turbo 2.1.4 and libtiff 4.4.0 built in | Image input for Tesseract | Leptonica (BSD-2-Clause style), libpng, zlib, IJG/BSD-3-Clause, libtiff | `licenses/leptonica/*.txt` |
| [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) language files | OCR language data, downloaded when first needed (not shipped) | Apache-2.0 | `licenses/tesseract-apache-2.0.txt` |
| [Sarabun](https://github.com/cadsondemak/Sarabun) Regular, Light and Bold (`Assets/Fonts/Sarabun-*.ttf`, from [google/fonts](https://github.com/google/fonts/tree/main/ofl/sarabun)) | Thai font put in forms with Thai fields, for viewers that redraw them; used for edited Sarabun text and to name Thai marks in Sarabun PDFs | SIL Open Font License 1.1 | `licenses/sarabun/OFL.txt` |

The installer also asks Windows to install the **Microsoft Visual C++ Redistributable** (needed by the
OCR libraries) when it is missing. It is Microsoft's own installer under Microsoft's license; it is not
part of the portable package.

## Maintaining this list

- Run a release build and compare the files in the publish folder with this table before each release;
  add the license text of anything new to `licenses`.
- **PDFsharp is pinned to 6.2.4.** On nuget.org, 6.2.4 is MIT and 6.5.0 is `AGPL-3.0-only`; the
  `LICENSE` file in empira's repository now says "AGPL Dual License" (checked October 2026). Check the
  license of any newer version on nuget.org and in the repository before upgrading.
- MuPDF and Poppler were not used (AGPL / GPL). LibreDWG (GPL-3.0) and the ODA Drawings SDK (paid
  membership) were not used for DWG files. iText and other AGPL signing libraries were avoided: PDF
  signing uses our own incremental writer (`Pdf/Cos`) and .NET's cryptography.
- Prefer MIT, BSD, Apache-2.0 and SIL OFL for new packages.
- Text editing embeds subsets of fonts installed on the user's computer into the user's own documents.
  The font's embedding permission (OS/2 fsType) is checked first: "restricted" fonts are never embedded
  (Leelawadee UI is used instead) and "no subsetting" fonts are embedded whole. The program ships one font family,
  Sarabun in three weights (SIL OFL, which allows embedding and redistribution with its license); `PdfSharp.WPFonts.dll`,
  part of the PDFsharp package, contains small Segoe WP fonts that PDFsharp can fall back to.
- Velopack's `Update.exe` and `Setup.exe` are written in Rust. Velopack allows only permissive licenses for
  their crates (MIT, Apache-2.0, BSD, ISC, Zlib, Unicode-3.0, MPL-2.0 and similar, see its `deny.toml`)
  but does not publish a per-crate notice file; one can be generated from its `Cargo.lock` with
  `cargo about` if needed.
