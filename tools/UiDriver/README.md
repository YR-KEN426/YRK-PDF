# UiDriver: drive the real YRK PDF with Windows UI Automation

Used in 1.2.1 step 11b testing. Computer use was not available, so Claude drove a Release build in a normal window.

- `ui.ps1`: dot-source it in Windows PowerShell 5.1 (`. .\ui.ps1`). It expects the app build in `uiapp\` next to the script:
  `dotnet build src/Yrk.PdfStudio.App -c Release -o tools/UiDriver/uiapp` (do not commit `uiapp/` or `shots/`).
  Helpers: `Open-App <pdf>`, `Find <name>`, `Click-El`, `Click x y`, `Drag`, `Chord <vk>` (Ctrl+key), `Key <vk>`, `TypeU <text>` (Unicode/Thai), `Shot <name>`.
- Safety: input is sent only while YRK PDF is the foreground window (`Guard`), typing stops if focus leaves, and clicks outside the window are refused. Other windows (for example the Claude app) can take focus at any time.
- Lessons: `SendKeys` shortcuts do not reach the app, so use `Chord`/`Key`. Thai typing uses `TypeU`. PasswordBox needs `Key` with virtual keys and does not clear reliably, so reopen the dialog. Do not `Invoke()` a MessageBox button through automation because it blocks; press Enter instead. Common file dialogs: click the file-name box, `Chord 0x41`, `TypeU` the full path, then click Save.
- `FixtureGenerator.cs.txt`: a temporary xUnit test that writes the test PDFs (`text.pdf`, `new-items.pdf`, `legacy-120.pdf`). Copy it into `tests/Yrk.PdfStudio.Core.Tests/` as `.cs`, run with `YRK_UI_FIXTURES=<folder>` and filter `ZzUiFixtureGenerator`, then remove it. Thai test PDF: made with Word COM (`ExportAsFixedFormat`) with TH Sarabun New text (fake names only).
- The app uses the real data folder `%LocalAppData%\YRK\EasyPdf`. Test files show up in the recent list, and a real import changes user data, so ask Ken before importing.
