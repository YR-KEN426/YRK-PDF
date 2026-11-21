# WindowRenderer / step 1

The construction table in `WindowCatalog.cs` is compiled into both this tool and `AllWindowsLoadTests`. The coverage test checks all 40 concrete app windows. `SupportBrands` and `ThaiAddressFields` are UserControls, not separate windows.

`WpfTestApp` embeds the real `App.xaml` and loads its ResourceDictionary with the app pack URI as the base URI. Adding a merged dictionary to App.xaml requires no duplicate dictionary list. App startup/exit services are never run. The helper runs layout, translates the visual tree, visits tabs and opens ComboBox/DatePicker popup templates. Binding, ResourceDictionary and Markup traces (warning/error), plus dispatcher exceptions, fail the test. It restores culture, language, trace listeners and Application state on cleanup.

Use `--theme light|dark` in single-window or batch mode (default: `light`). The shared host loads the selected whole palette before Styles is parsed. `AllWindowsLoadTests` covers every available window in th/en and both themes; DigitalSignDialog uses two synthetic Digital IDs and is included in all four language/theme combinations; no window is excluded. Batch child processes receive the selected theme, which is also recorded in the manifest. Use separate output folders for each theme; existing images are never overwritten.

```powershell
dotnet run --project tools/WindowRenderer -- --all <scratchpad/baseline-1.2.4>
dotnet run --project tools/WindowRenderer -- --all <scratchpad/dark> --theme dark
dotnet run --project tools/WindowRenderer -- --allow-any-window MainWindow th <outside-repo/example.png>
dotnet run --project tools/WindowRenderer -- --theme dark --allow-any-window MainWindow en <outside-repo/example-dark.png>
dotnet run --project tools/WindowRenderer -- --list
```

Batch mode isolates each window/language in a child process (45-second limit), writes PNGs, per-case diagnostic logs and `manifest.json`, and returns nonzero for any unavailable/error case. Existing PNGs are never overwritten. A PNG may still be captured when bindings fail; its exit code and log retain the defect. Store all output outside the project. The renderer uses synthetic document names/paths, fresh settings objects and temporary certificate stores. DigitalSignDialog uses the internal fixed-list DigitalIdStore constructor with a fictional .pfx file and an expired fictional Windows certificate. It never enumerates real Digital ID folders or the Windows certificate store. Its catalog factory also prevents Program.cs from taking an app-data folder snapshot. No signing, printing, importing, exporting or settings-save action is invoked.

## Construction / exclusion table

| Window | Fixture | Exclusion reason |
|---|---|---|
| MainWindow | Empty MainViewModel, no personal signatures or recent files |  |
| AboutDialog | AboutModel from fixture MainViewModel |  |
| AmountWordsDialog | Placement enabled |  |
| BackupDialog | UI only; no backup action |  |
| BatchDialog | Empty file list |  |
| CertifiedCopyDialog | Empty signature list |  |
| CompareWindow | Two synthetic one-page documents with example names |  |
| CompressDialog | Default options |  |
| ConvertScanDialog | Empty input list |  |
| ConvertScanResultDialog | Example output paths and text |  |
| CreateDigitalIdDialog | UI only; no certificate created |  |
| DigitalSignDialog | Two synthetic IDs: fictional .pfx file and expired Windows certificate; no real folder/certificate store reads |  |
| ErrorDialog | Catalogue error with no real exception/path |  |
| ETaxInvoiceDialog | Fresh in-memory settings |  |
| ExportDialog | Three pages, current page zero |  |
| FeedbackDialog | Example version |  |
| FileListDialog | Combine PDFs, empty list |  |
| FormFieldDialog | Example text field |  |
| ImportDialog | UI only; no import selected |  |
| LengthDialog | Millimetres |  |
| MessageDialog | Save changes with explicit default and dismissal results |  |
| OcrDialog | Default language choices; no OCR/download |  |
| OcrReviewDialog | Synthetic document and one uncertain example word |  |
| PageMarksDialog | Synthetic document and example filename |  |
| PasswordDialog | Example filename |  |
| PrintDialog | Synthetic one-page document and example printer name; no print submitted |  |
| PrivacyDialog | UI only; no clear action |  |
| PrivacyPolicyDialog | UI only |  |
| ProtectDialog | UI only |  |
| ReaderBookmarkDialog | Example bookmark |  |
| ScanCleanupDialog | Example page classifications |  |
| ScanDialog | Default constructor uses FakeScannerService, no scan requested |  |
| ShortcutsDialog | UI only |  |
| SignAgainDialog | One signature, no edits |  |
| SignatureDialog | Empty signature canvas, no PDF import |  |
| SplitDialog | Three pages, example output folder and bookmark |  |
| StampDialog | Fresh in-memory stamp settings |  |
| SupportDialog | UI only |  |
| TrustCertificateDialog | Ephemeral self-signed example certificate |  |
| TrustedCertificatesDialog | Empty isolated certificate store |  |


## Step 1 findings (application bugs intentionally not fixed)

| Window / issue | Evidence | Status |
|---|---|---|
| DigitalSignDialog (th/en, light/dark) | Internal fixed-list DigitalIdStore supplies only synthetic IDs. | Step 6: included in layout tests and renderer; the old skipped theory was removed. Historical step 1 PNG counts below predate this change. |
| ScanDialog (th/en) | `SelectedScope` initially has null Content, while `PresetChip` binds AutomationProperties.Name to Content. PresentationTraceSources reports Data Error 5. Popup/tab traversal also reports ComboBoxItem ancestor alignment bindings (Data Error 4). | Tests remain failing; baseline PNGs are retained with diagnostic logs. No app fix or diagnostic suppression. |
| PageMarksDialog (observed in th/en) | PresentationTraceSources intermittently reports Data Error 2: RotateTransform.Angle cannot find its governing FrameworkElement/DataContext during preview lifecycle. | Intermittent test failure retained; not skipped or filtered. Needs investigation before step 2. |

`RecentFiles.UseStorage(path, utcNow)` is internal and scoped to the execution context. The app harness redirects its recent list, and tests cover nested scopes, JSON reload and disposal after an await. Production still uses the same default path, paths-only JSON, filtering, ordering and 12-item limit. RecentFiles does not currently use timestamps; its injected clock is available for the planned Home history work without changing current behavior.

Baseline output: 76 top-level PNGs (38 windows x th/en), with `manifest.json` and per-case logs in the requested external scratchpad `baseline-1.2.4` folder. Earlier attempts are preserved in its `previous-attempt` subfolder. No images are added to the project. Print screenshots use an example printer name as well as synthetic document paths.

Historical step 1 verification (before step 6): the four new test classes contain 97 runner cases (including one explicitly skipped theory); all text, form, page operation and RecentFiles cases pass. The related run also includes AppSaveTests and AppTabTests because the harness now redirects RecentFiles. WPF diagnostics are left visible as failing tests. See the external TRX results for exact per-run counts; intermittent preview diagnostics can change the failure count.



Existing suite skips (unchanged; included in the full-suite skip count):

| Existing test | Reason reported by the runner |
|---|---|
| SavedItemEditingTests.Saved_item_reviewer_screenshots | Requires explicit YRK_SHOT_DIR opt-in. Baselines for this task use WindowRenderer instead. |
| AppLogTests.LogMessage_does_not_follow_a_daily_file_reparse_point | File symlink creation privileges unavailable in this process. |
| ScanPreviewBenchmarkTests.Slider_update_cost | Requires YRK_SCAN_BENCH=1 opt-in for the performance benchmark. |

Historical step 1 full solution run: `dotnet test YrkPdfStudio.slnx --no-restore -p:RunAnalyzers=false` — 2,368 passed, 4 failed, 4 skipped, 2,376 total. All four failures are the ScanDialog and PageMarksDialog window cases in th/en. Full TRX: external temp folder `yrk-step1-results/step1-full-final.trx`.

Historical step 1 related run (four new classes plus AppSaveTests and AppTabTests): 115 passed, 4 failed, 1 skipped, 120 total. Of these, the new classes account for 97 cases: 92 passed, 4 failed, 1 skipped. Related TRX: external temp folder `yrk-step1-results/step1-related-latest.trx`. `git diff --check` passes. No plan checkbox or commit was made.

## DPI and screen size (step 6)

Use `--dpi 100|125|150|200` in single-window and `--all` modes (default: 100 percent; unsupported, missing or repeated values print usage and exit 2). The value is Windows scaling percent: 150 means 144 actual DPI. `--size WxH` specifies physical screen pixels, not DIP. Layout dimensions are pixels * 100 / scale-percent (equivalently pixels * 96 / actual-DPI): 1366x768 at 150 gives 910.67x512 DIP. Window Width/Height receive those layout dimensions, with MaxWidth/MaxHeight constrained to the available work area after subtracting a 40 DIP taskbar. Oversized windows fail rather than being silently cropped.

The renderer attempts a process-local reflection override of WPF SystemParameters' cached WorkArea before constructing windows, so constructor limits such as WorkArea.Height - 24 see the simulated work area. The desktop itself is never changed. If that internal cache field is unavailable on a different WPF runtime, WorkArea is not simulated (a diagnostic is printed); with --size the renderer still enforces MaxWidth/MaxHeight with a 40 DIP taskbar allowance. RenderTargetBitmap uses actual DPI (96 * scale-percent / 100), with pixel dimensions calculated from actual window DIP dimensions at that scale.

Batch PNG/log names include `.dpi150` (or the selected value); manifest entries and console output record the scale. Single-window mode preserves the explicitly requested output filename and reports scale and actual DPI in the result text. Existing files are never overwritten. Use separate output directories for themes/screen sizes.

```powershell
dotnet run --project tools/WindowRenderer -- --all <outside-repo/dark-150> --theme dark --size 1366x768 --dpi 150
dotnet run --project tools/WindowRenderer -- --allow-any-window DigitalSignDialog en <outside-repo/sign.dpi150.png> --size 1366x768 --dpi 150
```

Step 6 verification: solution and WindowRenderer builds pass. `dotnet test tests/Yrk.PdfStudio.Core.Tests/Yrk.PdfStudio.Core.Tests.csproj --no-build --no-restore --filter AllWindowsLoadTests --blame-hang-timeout 5m` passed 167 cases, with zero failures/skips. Renderer smoke checks passed at 100/125/150/200 percent; DigitalSignDialog at 1366x768/150 produced a 1366x672 PNG (its constructor reserves another 24 DIP below WorkArea). The WorkArea cache override took effect on this runtime. Invalid/missing/duplicate DPI returned usage exit 2; an oversized window failed without writing a PNG.
