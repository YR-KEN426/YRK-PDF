# YRK PDF: architecture

## Technology choices

| Area | Choice | Why |
|---|---|---|
| Language / UI | C# on .NET 10 (LTS), WPF, MVVM | Fast to develop; direct access to Windows features (certificate store, USB tokens, printing, file associations) |
| Rendering, text, forms, annotations | PDFium | Same engine as Chrome; BSD/Apache license allows commercial use |
| Structural editing, merge/split, signature dictionaries | PDFsharp 6 (MIT), planned | Writing incremental updates and new objects |
| Digital signatures | `System.Security.Cryptography.Pkcs` (CMS), planned | Built into .NET; works with the Windows certificate store and CNG tokens |
| Text shaping for editing | HarfBuzzSharp, planned | Correct placement of Thai vowels and tone marks |
| OCR | Tesseract (tha + eng), planned | |
| Installer / updates | Velopack (per-user, delta updates) + portable zip, planned | |

## Layers

```
UI (WPF views)           MainWindow, dialogs
View models              MainViewModel, PageViewModel, ...          (no PDFium types)
Services                 DialogService; later: Annotation, Form, Signature, PageOps, OCR
Engine facade            IPdfEngine / IPdfDocument                  (Yrk.PdfStudio.Core)
Engine implementation    PdfiumEngine: one dedicated thread, prioritized queue
Native                   pdfium.dll
```

Rules:

1. The UI never calls PDFium directly; everything goes through `IPdfEngine`. The engine can be swapped
   or moved into a separate sandboxed process later without touching the UI.
2. PDFium is not thread-safe. `PdfiumEngine` owns one worker thread; interactive work (visible pages)
   runs before background work (thumbnails, search). Cancelled work is skipped before it starts.
3. PDF files are untrusted input: native exceptions never cross the callback boundary, bookmark trees
   are depth- and cycle-limited, and (later) JavaScript stays disabled by default.
4. Files are read through a streaming callback (`FPDF_LoadCustomDocument`), which keeps memory low and
   supports Unicode (Thai) paths.

## ISO 32000 mapping

| Spec section | Topic | Where it lands |
|---|---|---|
| §7.3–7.5 | Objects, xref, trailer, incremental updates | Opening, repair, save strategy |
| §7.6 | Encryption | Password prompt (done), permissions (later) |
| §7.7 | Page tree | Page operations |
| §8–§11 | Graphics, text, rendering, transparency | PDFium |
| §9.6–9.10 | Fonts, CMaps, ToUnicode | Search, copy, text editing |
| §12.3 | Outlines | Bookmarks panel (done) |
| §12.5 | Annotations | Comments (Phase 4) |
| §12.7 | AcroForm | Fill & Sign (Phase 2) |
| §12.8 | Digital signatures | PAdES signing and validation (Phase 5) |

## Save strategy

| Situation | Method |
|---|---|
| Annotations, form filling, signing | Incremental update (§7.5.6), so existing signatures stay valid |
| Save As, optimize, page deletion | Full rewrite |
| Always | Write to a temporary file, then replace atomically |

## E-signatures

- **Electronic signature:** draw (Windows Ink), type with a handwriting font, or import an image.
  Saved signatures are encrypted with DPAPI. Includes a one-click "สำเนาถูกต้อง" (certified copy)
  stamp, rubber stamps (อนุมัติ, ได้รับแล้ว, จ่ายแล้ว ... with a Thai B.E. date, and the user's own stamps kept
  in stamps.json) and a usage watermark for ID-card copies.
- **Digital signature (PAdES B-B / B-T today, B-LT planned):** .pfx files, the Windows certificate store and USB
  tokens from Thai CAs; RFC 3161 timestamps; a signature panel that validates received documents.

## Fonts

- Bundled (SIL OFL): Sarabun, Noto Sans/Serif Thai, Liberation Sans/Serif/Mono (metric-compatible
  substitutes for Arial/Times/Courier), and handwriting fonts for typed signatures.
- Missing fonts: substitute automatically, show a notice, offer a download from Google Fonts.
- Check OS/2 `fsType` embedding rights before embedding any system font.

## Roadmap

| Phase | Scope |
|---|---|
| 1 | Reader: open, render, zoom, thumbnails, bookmarks, search (**done**) |
| 2 | Fill & Sign: forms, electronic signatures, certified-copy stamp, save (**done**) |
| 3 | Page tools: merge, split, rotate, reorder, images to PDF, compress (**done**) |
| 4 | Annotations: highlight, comments, ink, shapes (**done**) |
| 5 | Digital signatures (PAdES B-B/B-T/B-LT), certification (DocMDP), validation, USB tokens (**done**; OCSP and B-LTA later) |
| 6 | OCR, true redaction, text editing (**done**; scanner input moved to a later phase) |
| 7 | Installer, auto-update, code signing (**done**; v1.0 needs a real signing certificate and a feed URL) |

### Planned (not started; order to be decided)

Windows first: the program should be stable before new platforms or paid distribution.

**For Thai offices (what sets YRK PDF apart)**
- e-Tax Invoice, next steps: XAdES signature of the XML itself (for the e-Tax Invoice & e-Receipt system), structured Thai
  addresses (province, district and subdistrict codes), discounts, credit and debit notes (80, 81), receipts (T01).
  Done: the form, the invoice PDF in Sarabun, PDF/A-3b with the ETDA XML (ขมธอ. 3-2560 v2.0) attached as "Source".

**Scans** (done: straighten, trim dark edges, delete blank pages; split by bookmarks)
- Scanner input (TWAIN/WIA) straight into a PDF.

**Polish**
- Dark mode.

**Signatures**
- OCSP revocation data and document timestamps (PAdES B-LTA).
- Done: signing and redacting password-protected files (RC4, AES-128, AES-256), keeping the same key and passwords.
  Still blocked with "Save an unprotected copy first": real text, form field looks, OCR and page marks on encrypted files.

**Distribution** (on hold until the program is stable)
- Code signing; or the Microsoft Store, which may avoid SmartScreen warnings at lower cost (check its
  current terms first).
- Windows on ARM (OCR is not supported there yet).

**Later platforms**
- Android: a small app to read, fill in, sign and stamp PDFs, with the same Thai support, reusing
  `Yrk.PdfStudio.Core`.
- Mac (Avalonia UI on the same core), once sponsorship covers the Apple Developer fee.

### Phase 2 design notes

- **Fill & Sign rework (vector signatures, real text).** A signature is a `SignatureArt`: vector
  outlines (`PdfVectorArt`), the first page of a PDF, or an image, plus a preview bitmap for the screen.
  Drawn strokes use `Stroke.GetGeometry` (the pressure-shaped outline InkCanvas draws) and typed names
  `FormattedText.BuildGeometry`, converted to move/line/cubic commands (quadratics raised to cubics,
  arcs flattened). SVG import maps shapes and strokes (widened to outlines) through the same path, with
  transforms and fill rules. `AddVectorAsync` builds PDFium path objects in a unit square and places them
  with the same matrix as images, so they stay upright on rotated pages. `AddPdfPageAsync` shrinks the
  source page's boxes to the ink and imports it with `FPDF_NewXObjectFromPage` (the form's BBox clips,
  and its origin is the crop's lower-left corner). Added text is kept as an overlay through the commit
  and written into the saved bytes with `PdfTextWriter` (shaped like the preview, Leelawadee UI subset);
  encrypted files and page tools still flatten it as an image. Saved signatures are JSON behind a
  "YRKSIG2" marker (old ones are plain PNG), encrypted with DPAPI as before.

- **Overlays until save.** Signatures, stamps and text are view-model overlays (page-relative
  coordinates) that can be moved, resized and undone. Saving draws them into the page content with
  `FPDFImageObj_SetBitmap` + `FPDFPage_GenerateContent`; the matrix comes from `FPDF_DeviceToPage`, so items
  stay upright on rotated pages.
- **Forms through PDFium's form engine.** Values are set by focusing the widget and replacing its text,
  clicking check boxes, or selecting an option (`FORM_*`), so PDFium regenerates appearances. The form
  environment is created with every callback null, which also keeps form JavaScript disabled.
- **One PDFium thread per process.** PDFium has global state; all engine instances share
  `PdfiumWorker`, which runs interactive work before background work.
- **Safe save.** In-memory changes → incremental update to a temporary file next to the target →
  close the document → atomic replace → reopen at the same page and zoom. If any step fails, the original
  file is untouched and the document is reloaded with the overlays still pending.

### Phase 3 design notes

- **Page structure through PDFium** (`FPDFPage_SetRotation`, `FPDFPage_Delete`, `FPDF_MovePages`,
  `FPDF_ImportPagesByIndex`). After each change the page cache is dropped and `PageSizes` is replaced as
  one snapshot (`PageCount` derives from it), then the view rebuilds its pages.
- **Overlays are committed before page tools**, because page numbers change. Saving and page tools share
  `CommitOverlaysAsync`; once an overlay is written into the document it is removed, so a failed step
  can never add it twice.
- **Compression uses PDFsharp, not PDFium.** PDFium attaches a new stream when an image is replaced and
  writes every old object back out, so files do not shrink. PDFsharp rewrites the image stream in
  place and drops unreachable objects. JPEG decoding/encoding is supplied by the app (`IPdfImageCodec`,
  WIC through WPF), keeping Core free of UI libraries.
- **New files are written to a temporary file and moved into place**, so a failure never leaves a
  half-written PDF under the user's chosen name.

### Phase 4 design notes

- **Annotations go straight into the document** (`FPDFPage_CreateAnnot`), unlike Phase 2 overlays: they
  are real PDF objects, so the comments list, other viewers and saving all see the same thing. Each new
  annotation gets a GUID in /NM; edits, deletes and undo find it by that id, not by index.
- **Appearance streams are generated immediately** by drawing the page once into a 1×1 bitmap, which
  makes PDFium write /AP for annotations that lack one. Arrows are stored as ink (line plus a "V"),
  because PDFium cannot generate appearances for line annotations.
- **Coordinates go through `FPDF_DeviceToPage`**, so annotations land where they were drawn on rotated
  pages; text markup uses Acrobat's quad-point order (upper-left, upper-right, lower-left, lower-right).
- **Text selection** uses `FPDFText_GetCharIndexAtPos` with a wider second pass for clicks in the margin.
  The text page of the page being dragged on is cached and closed before its page is closed. Selection
  previews are requested one at a time and catch up with the latest mouse position.
- Page tools that renumber pages reset the undo history, because older steps refer to page numbers.

### Phase 5 design notes

- **PDFium cannot write signatures**, so `Pdf/Cos` is a small reader and writer of PDF objects of our
  own (no GPL/AGPL code): classic cross-reference tables and cross-reference streams (with /Prev chains,
  hybrid files and PNG predictors), object streams, and recovery by scanning for `n g obj` when the
  cross-reference data is broken. `CosIncrementalUpdate` appends changed and new objects plus a new
  cross-reference section of the same kind as the previous one; the original bytes are never changed.
- **Signing** (`PdfSigner`) adds a signature dictionary (`/Filter /Adobe.PPKLite`,
  `/SubFilter /ETSI.CAdES.detached`), a merged field-and-widget (`/FT /Sig`, `/F 132`), the widget to the
  page's /Annots (whichever object holds the array) and the field to /AcroForm /Fields, and sets
  /SigFlags 3. /ByteRange and /Contents are fixed-width placeholders whose final file offsets are
  recorded while writing; the byte range is patched in, the two ranges are hashed and signed, and the
  CMS is written into /Contents. If the CMS is larger than the reserve, signing runs once more with
  more room.
- **CMS (CAdES)** via `System.Security.Cryptography.Pkcs` (MIT): SHA-256, the whole certificate chain,
  signed attributes content-type, message-digest and ESS signing-certificate-v2. Following PAdES
  baseline there is no signing-time attribute; the time is in /M, or in the RFC 3161 token stored as the
  unsigned attribute signature-time-stamp (B-T). `silent: false` lets Windows prompt for a token PIN.
- **Visible appearance** is an image (as for Phase 2 stamps, so Thai is shaped by WPF) inside a form
  XObject whose /Matrix keeps it upright on rotated pages; rectangle and matrix come from the page's
  effective crop box and /Rotate, so placement matches `FPDF_DeviceToPage`.
- **Verification** (`PdfSignatureVerifier`) walks the AcroForm field tree, checks the byte range
  (starts at 0, the gap is exactly the /Contents string), the CMS signature and message digest, the
  signing-certificate-v2 hash, the timestamp token, and the certificate chain with `X509Chain`
  (verification time = timestamp time when there is one). It also supports `adbe.pkcs7.detached` and
  `adbe.pkcs7.sha1`. The app runs a quick offline pass first, then a second pass with online revocation.
- **Refusals:** encrypted documents (strings would need encrypting) and documents certified with
  DocMDP P=1 (no changes allowed).
- Next: B-LT/B-LTA (DSS with OCSP/CRL and document timestamps), certifying signatures, signing an empty
  signature field that is already in the form, and judging which changes after a signature are allowed.

### Phase 6 design notes

- **OCR** uses Tesseract 5 through the `Tesseract` NuGet package (Apache-2.0; Leptonica is BSD-style) with
  tessdata_fast language files, downloaded on first use to `%LOCALAPPDATA%\YRK\EasyPdf\tessdata`. Pages are
  rendered at 300 dpi and fed as 8-bit grey BMP. Tesseract returns Thai as single characters (tone marks
  and upper vowels with their own boxes); pieces are joined up to each real space, so a run is placed as
  one string. The text layer is a Type0 font over an embedded "glyphless" TrueType generated in code (every
  code maps to one glyph through /CIDToGIDMap; codes are UTF-16 units, ToUnicode is the identity), in
  render mode 3, one Tm per run stretched over its box. The glyph is a rectangle, not empty: PDFium ignores
  rotated text whose glyphs have no outline.
- **Content streams** (`Cos/ContentStream.cs`) are tokenised with their byte ranges, so unchanged operators
  are copied byte for byte. `ContentFont` reads what is needed to place characters: code length (simple
  fonts, Identity-H, UCS-2 CMaps, embedded CMaps), /Widths and /W, ascent and descent.
- **Redaction** (`PdfRedactor`): `ContentRedactor` tracks the graphics and text state (CTM, Tm, Tc, Tw, Tz,
  TL, Ts, font) and rewrites text-showing operators as TJ arrays without the characters whose box overlaps
  an area by at least 20 %, adding the removed widths as position adjustments so nothing else moves. Paths
  entirely inside an area are dropped; form XObjects are redacted recursively into new copies; ActualText,
  Alt and E are stripped from marked content. Images or inline images under an area, unmeasurable fonts,
  or any text PDFium still finds in an area (`FPDFText_GetBoundedText`) make that page an image of itself
  (200 dpi JPEG via WIC) with the areas blacked out. Annotations under an area are removed, and their
  fields leave the AcroForm. `CosCompactWriter` then writes a fresh single-revision file with only the
  objects reachable from the trailer, which is verified once more before it is returned.
- **Real text** (`Text/PdfTextWriter`): WPF shapes the text (`FormattedText` drawn into a `DrawingGroup`,
  then its `GlyphRun`s), which gives DirectWrite's Thai mark placement and font fallback.
  `GlyphTypeface.ComputeSubset` produces the font subset (glyph ids kept); fonts that forbid embedding or
  have CFF outlines fall back to Leelawadee UI. Character codes are allocated per distinct (glyph, text)
  pair, mapped to glyphs by /CIDToGIDMap and to text by /ToUnicode, so a mark glyph extracts correctly in
  every context. Within a cluster, glyphs get characters through the font's cmap, the rest aligned to the
  end (Thai SARA AM). Each run is one Tm and one TJ array with shaped advances as adjustments: word
  processors write Thai this way and viewers then extract it in order. A run with a raised mark or a glyph
  without a character (stacked marks, SARA AM split) is drawn as glyph outlines over the same TJ in render
  mode 3, because viewers mis-order text written with rises or split text objects.
- **Editing a line** (`PdfTextEditor`): PDFium finds the line at the click (generated line breaks,
  `FPDFText_GetCharBox`, font size, fill colour and font name), the redactor removes its characters
  without black boxes, and the writer puts the new text on the old baseline. Edits are overlays until saved
  and are applied to the saved bytes as incremental updates.
- Next: Fill & Sign text as real text, paragraph reflow, a font picker, redaction of bookmarks and
  metadata, and scanner input (WIA).

### Drawings and layers design notes

- **Layers** (`Layers/PdfLayers`): /OCProperties is read with our COS reader (/OCGs, /D with /ON, /OFF,
  /BaseState, /Locked, and the nested /Order with headings). PDFium has no API to switch optional content,
  so `PdfiumDocument` renders hidden-layer views from a copy of the current document (saved to memory,
  including unsaved edits) whose /D is rewritten, loaded as a second PDFium document. A change counter
  rebuilds the copy after any edit. Null visibility means "the file's own default", so the main document
  renders directly. "Save as default" applies the same /D rewrite to the saved file.
- **Detail rendering:** whole-page renders stay capped at 24 MP. When a page is larger than that at the
  current zoom, the view reports the visible part after scrolling pauses (150 ms), and
  `RenderPageRegionAsync` renders just that part (plus a 25 % margin) at full resolution, by drawing the
  full-size page at a negative offset into a small bitmap. It sits over the capped image, which gives
  sharp text up to 6400 %.
- **DWG/DXF** (`Cad/CadToPdf`): ACadSharp (MIT) reads the file, and the Thai and other code pages are
  registered for it. Model-space entities are walked with a context carrying the block transform and what
  ByBlock and layer "0" resolve to. 2D entities go through AutoCAD's arbitrary-axis OCS, arcs, circles and
  ellipses become exact cubic Béziers, splines are evaluated with de Boor (rational), and bulges become
  arcs. Hatch patterns are drawn as dashed line families clipped to the boundary (W*). Everything is
  written in drawing order, with one optional content group per CAD layer, at a standard scale that fits
  A0, with a /VP /Measure dictionary. Text is returned separately: the app shapes it with WPF (capital
  height = AutoCAD text height, alignment, rotation, width factor) and `PdfTextWriter` writes it as real
  text inside the layer's marked content. The result is compacted into one revision and opened from a temp
  file; Save asks for a PDF location.
  **Layouts:** drawing goes into "sheets" (model first, then each paper-space layout by tab order; empty
  ones are dropped). For each viewport (not the paper viewport, id 1; plan views only) the model space is
  drawn again with matrix model → (minus view target, view twist) → (zoom = height / view height, view
  centre onto viewport centre), shapes tagged with the frame as their clip, and layers frozen in the
  viewport skipped. `WriteContent` closes the layer's marked content before changing the clip (`q … re W n`
  / `Q`) so BDC/EMC and q/Q stay nested. Each page gets a /VP for the whole page plus one per viewport
  (model mm per point = ratio / points per mm); `PdfPageScale.At` picks the smallest containing one. Texts
  carry their page index; the outline has one bookmark per page.
- **Measuring** (`Measure/PdfMeasure`): `PdfScaleReader` reads each page's /VP viewports with an /RL
  /Measure dictionary: the first /X number format gives the factor (C) and unit (U), and /BBox, mapped to
  the displayed page, limits where it applies. The app measures in PDF points from the overlay's
  normalised coordinates, multiplies by the scale (from the file, a preset, or a calibration) and converts
  units. Lines and labels are drawn in page points with a thickness of 1/zoom, so they stay the same on
  screen. "Add to document" writes ink annotations whose contents are the results.
- **Compare** (`Compare/PdfCompare`): both pages are rendered at the first page's size (up to 3 px/pt,
  20 MP). A pixel is ink above a darkness threshold; it counts as changed only when the other version has
  no ink within one pixel (dilation), which ignores anti-aliasing and one-pixel shifts between exports.
  Changed pixels are grouped in 8 px cells, and nearby cells are joined into regions (largest first). The
  compare window opens its own copies of both files so the main view stays usable; DWG/DXF go through the
  same converter as Open.
- **Snapping** (`PdfiumDocument.Snap`, `Measure/PdfSnap`): the page's path objects are walked with
  their matrices (form objects recursively, so CAD blocks count); move/line points and Bézier end
  points become "end" points, the middles of straight segments (and of the closing segment) "middle"
  points. One page-space → display mapping is taken from FPDF_PageToDevice at a large device size, so
  rotation is handled and rounding is negligible. Points are de-duplicated and kept in an 8-point
  grid (`PdfSnapIndex`); a query looks at the cells around the cursor (31,000 points on the sample
  drawing load in about 40 ms). The app snaps within 10 screen pixels and prefers ends over middles.
- **Rulers** (`Controls/Ruler`): a light FrameworkElement that draws 1-2-5 major steps at least 60 DIPs
  apart with minor ticks, the page extent and the mouse position; the view sets its origin from the
  page container's position and its scale from the same scale lookup as measuring.
- Next: paper-space layouts with viewports, MText formatting and wrapping, multileaders and tables,
  monochrome option in the UI, intersection snapping, and a text-level compare.

### Watermark, header and footer design notes

- `PageMarks.Layout` (App) computes every mark's text, shaped line and top-left corner in page points;
  the dialog preview draws the same results with TextBlocks, so preview and file agree. A fitted
  watermark is shaped at 100 pt and scaled until its turned bounding box uses 80 % of each side, then
  centred by turning the box's middle around its corner.
- `PdfTextBlock` gained `Opacity` (an ExtGState with /ca and /CA per opacity) and `Behind`: such text
  goes into content prepended by `CosPageEdit.Prepend`, in its own q/Q before the page's old content.
  Marks are added as an incremental update by `PdfTextWriter`; encrypted files must be unprotected first.

### Password protection design notes

- `PdfSecurity.Protect` implements the standard security handler, revision 6 (V5, AESV3): a random
  256-bit file key; U/UE and O/OE from the hardened SHA-2 hash (algorithm 2.B) with random salts; /Perms
  encrypted with AES-ECB; P with the unused bits set as required. Passwords are UTF-8 (NFKC), at most
  127 bytes. Without an owner password a random one is used, so restrictions cannot be lifted.
- `CosCompactWriter` takes an optional `CosEncryption`: every string and stream (after its filters) is
  encrypted with AES-256-CBC and a random IV, the /Encrypt dictionary is written in the clear as the last
  object, the trailer gets /Encrypt and an /ID, the header is 1.7 and the catalog declares Adobe
  extension level 8.
- Decrypting is PDFium's job: `SaveDecryptedAsync` saves with FPDF_REMOVE_SECURITY, which keeps edits
  made in memory. `Permissions` comes from FPDF_GetDocPermissions, which is everything when the file was
  opened with the owner password; that is also how the owner password is verified before reopening.
- Saving needs Modify, or only FillForms when the sole changes are form values; comments count as
  changes (a little stricter than Annotate alone).

### Language design notes

- English is written in XAML and code as usual and is also the key: `Localization/th.json` (embedded,
  comments allowed) maps English to Thai, and a missing key shows English. `Loc.T(text)` translates,
  `Loc.F(format, args)` translates a composite format, so word order can differ in Thai. Plurals are
  separate keys ("1 page" / "{0} pages").
- XAML needs no markup: `UiTranslator` walks each window's logical and visual tree after layout changes
  (at most every 250 ms, and only once Thai has been used) and translates literal Text, Content, Header,
  ToolTip, Title, AutomationProperties.Name and Run texts. Bound values are skipped, the English original
  is kept per element so switching back works, and `Tag="keep-text"` exempts an element (language
  names). WPF raises Loaded only on elements with their own handler, which is why this is not a Loaded
  class handler. Menus and tooltips (their own popups) are translated when they open.
- Fixed texts sent to the status bar, message boxes, progress and busy helpers are translated there, so
  call sites only use `Loc.F` for texts with values. View models raise a property change for everything
  when the language changes. Bound English values (enums, scale names, OCR languages) use
  `TrConverter`.
- `build/check-translations.ps1` extracts the texts from XAML and from C# places that are translated, and
  lists those without Thai (a short list of names stays English on purpose).

### Print design notes

- `PrintLayout` (Core) places a page on the paper: optional 90° turn when page and paper orientation
  differ, sizing (fit, actual, shrink oversized, custom) and centring in the printable area. The dialog
  preview and the print job use the same calculation.
- `RenderPageForPrintAsync` renders with PDFium's FPDF_PRINTING flag (only printable annotations, no
  form highlight), optionally FPDF_GRAYSCALE and without annotations; hidden layers use the same layer
  view as the screen.
- `PdfPrinting` (App) prints through System.Printing/XPS on its own STA thread: the print ticket gets the
  paper, orientation, copies, collation and colour, and a `DocumentPaginator` renders each page only when
  the spooler asks for it, so memory stays at one page. That thread has a WPF dispatcher context, so the
  render is started from the thread pool before waiting on it (waiting directly would deadlock).
- Margins come from the printer's imageable area for the paper and are kept symmetric.

### Phase 7 design notes

- **Velopack** (MIT) instead of MSIX or Inno Setup: per-user install without admin rights, delta updates,
  a portable zip from the same build, and a Rust Setup/Update.exe that does not need .NET. The library and
  the `vpk` tool (local tool in `dotnet-tools.json`) must have the same version.
- **Startup:** `Program.Main` (the `StartupObject`) runs `VelopackApp.Build().Run()` before WPF starts, so
  install, update and uninstall hooks run and exit without showing a window. The hooks register or remove
  the per-user "Open with" entries (`FileAssociation`: ProgId, OpenWithProgids, Applications,
  Capabilities and RegisteredApplications under HKCU). Windows protects the default PDF viewer choice, so
  the app never sets it.
- **Updates:** the feed URL is baked into the build (`-p:YrkUpdateUrl`, read back as assembly metadata) and
  can be overridden with `YRK_PDFSTUDIO_UPDATE_URL`. `AppUpdater` uses a web folder, GitHub Releases or a
  local folder. The app checks 20 s after start (if enabled in About), downloads quietly, and shows a
  banner; "Restart to update" first asks to save unsaved work. A downloaded update that is not applied is
  installed on the next start. Failures are only logged.
- **Publishing:** self-contained win-x64 with ReadyToRun (faster start), only English and Thai framework
  resources, and only the matching Tesseract native folder. Setup installs `vcredist143` if needed.
- **Signing:** vpk signs the app's own files, Update.exe and Setup.exe. `build\sign.ps1` uses
  `Set-AuthenticodeSignature` with a .pfx, so no Windows SDK is needed. The template goes to vpk through
  `VPK_SIGN_TEMPLATE`, because Windows PowerShell breaks quoted native arguments. Azure Trusted Signing and
  signtool (`VPK_SIGN_PARAMS`, for hardware tokens) are supported too.
- **Icon:** drawn by `build\make-icon.ps1` (WPF) into `Assets\app.ico`, with BMP frames up to 128 px and a
  PNG frame at 256 px, as older icon readers expect.
- Next for v1.0: a code-signing certificate, a download site and feed, a win-arm64 build (needs an ARM
  Tesseract), Thai UI text, and an optional MSI for companies.

Phase 1 follow-ups: recent files and tile rendering for very high zoom.

### Count tool

`AnnotationTool.Count` (Measure tab). Marks live in `MainViewModel.Count` in click order and are shown per
page (`PageViewModel.CountMarks`), drawn at a fixed screen size like measurement labels. A click within a
few pixels of a mark removes it; numbers are per type in click order. Totals and the Copy / Excel table come
from `Core/Pdf/Measure/CountTally` (tested on its own). Marks are not saved unless added as circle comments.
Like measurements, they are cleared when pages are rotated, moved or deleted. No snapping: symbols rarely
have a snap point at their centre.

### Creating forms

`Core/Pdf/Forms/PdfFormBuilder` adds fields with `CosIncrementalUpdate`: each is a field merged with its
widget (T, FT, Rect, F print, P, MK border, Ff for required / multiline / combo), appended to the page's
/Annots (or its indirect array) and AcroForm /Fields. DR gets Helv and ZaDb; NeedAppearances is false
because every widget has an /AP: a thin frame, a ZapfDingbats check for the /Yes state, a down-arrow for
dropdowns, a dashed frame for signatures. Names already used get " 2", " 3"… (dots would nest fields).
Thai typed into the fields later is drawn by PDFium, which adds a font for the script to DR as needed.
In the app the fields are drafts (`FormFieldDraftViewModel`) until saved, then applied to the saved bytes
after text edits and layer defaults, like the other byte-level changes.

### PDF/A and batch

`Core/Pdf/Archive/PdfA` rewrites the file through the COS layer (`CosDocument.Replace` / `AddObject`, then
`CosCompactWriter` with a 1.7 header): catalog and annotation fixes, one walk over every reachable object
(ExtGState transfer functions, image interpolation and alternates, LZW → Flate, external streams,
PostScript, CMYK images and `k`/`K`/`cs /DeviceCMYK` in content), fonts, the sRGB output intent
(`SrgbProfile`: an ICC v2 profile built from the IEC 61966-2.1 colorimetry, no third-party file) and XMP
metadata mirrored from the Info dictionary with identical dates. Unembedded simple fonts become TrueType
with the installed .ttf as FontFile2 (`IPdfAFontSource`, implemented with WPF `GlyphTypeface` in the app),
WinAnsi encoding (standard-encoding quotes and existing /Differences kept, glyph names via `GlyphNames`)
and /Widths from the embedded font. Problems are collected as `PdfANote` templates (translated in the app)
and stop the conversion. Validation: veraPDF (PDF/A-2b profile) is used as a local test tool only.

`MainViewModel.Batch` asks for the job's settings once with the existing dialogs, then opens each file
with the engine and runs the same core functions the single-file commands use, writing to unique names
in the output folder (`UniquePath` never returns the source or an existing file).

### Export

`IPdfDocument.GetPageTextSpansAsync` returns the page's text as styled spans with positions in points
(PDFium char boxes, fonts and colours; Thai marks stay with their letter). `Core/Pdf/Export/TextLayout`
rebuilds lines (vertical overlap), paragraphs (line gap, size change, short last line, indents; Thai
lines are joined without a space) and tables (column bands from the extents of cells on multi-cell lines,
so right-aligned numbers share a column). `DocxWriter` and `XlsxWriter` write the Office Open XML parts
directly with `ZipArchive`; no Office library or Office install is needed. Images are rendered as for
printing and encoded with WPF in the app (`Services/PdfExporter`), capped at 100 megapixels per page.
Files are written to a temporary name and moved into place.

### Tabs

Each open file is its own `MainViewModel` (`MainViewModel.Tabs.cs`); all share the one PDFium engine and
the saved signatures. `MainWindow` keeps the list, shows one at a time by swapping `DataContext` and its
event subscriptions (`Attach` / `Detach`), and restores each tab's scroll position. Open and Close in a tab
raise `OpenInNewTabRequested` / `CloseTabRequested`, so the window decides where a file goes (an empty tab,
the tab already showing it, or a new one). The update banner is shared through a static.

**One window:** `Services/SingleInstance` holds a per-user mutex. A second launch sends its arguments
(full paths) over a named pipe created with `PipeOptions.CurrentUserOnly`, so only the same user on this
computer can reach it, and exits; the running app opens them in tabs. No network is involved.
