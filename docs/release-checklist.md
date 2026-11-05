# Release checklist for Ken

## Test the update before release

1. Build and sign a real **1.1.5 test feed** with `build\release.ps1`, using your own key and **no `-Publish`**. This is Ken's job; the tampering script never needs the key.
2. From the repository root, run:
   ```powershell
   powershell -File build\make-tampered-feed.ps1 -Feed <feed> -FixFeedHash
   ```
   Replace `<feed>` with the quoted feed folder path. Recommend `-FixFeedHash`: it refreshes Velopack's package hashes/size so the modified package can reach the app's full-file signature check. Running once without it is optional; stale metadata may cause Velopack to reject first. Existing output folders require `-Force` to replace.
3. Install **1.1.4**. Confirm the actual install path; check `%LocalAppData%\YrkEasyPdf\Update.exe` (do not assume it). Record the hash before testing:
   ```powershell
   Get-FileHash "$env:LOCALAPPDATA\YrkEasyPdf\Update.exe" -Algorithm SHA256
   ```
4. Test each folder in turn: the valid feed, `<feed>-tampered-updateexe`, and `<feed>-tampered-sig`. Start every case from a clean **1.1.4** installation (restore/reinstall after the successful update), record its baseline hash, and close the app between cases. In PowerShell, set `$env:YRK_PDFSTUDIO_UPDATE_URL = '<absolute folder path>'`, launch the installed app from that **same window**, then choose **Help > Check for updates**. Use a folder path.
5. Expected: valid feed offers the update, shows the restart button, and installs **1.1.5** after restart. Each tampered feed shows an error, offers no restart button, and leaves the version at **1.1.4**.
6. After each tampered run, repeat `Get-FileHash` for the installed `Update.exe`: it must match that run's baseline. Check the package cache too: the rejected package must not remain (the app deletes it). `AppUpdater.cs` uses Velopack's `Locator.PackagesDir`; check the actual location, including a possible `packages` folder under the install root, rather than assuming its path.
7. Afterwards, run `Remove-Item Env:YRK_PDFSTUDIO_UPDATE_URL`. If you also set a persistent user variable (including via `setx`), remove it with `[Environment]::SetEnvironmentVariable('YRK_PDFSTUDIO_UPDATE_URL', $null, 'User')` and reopen affected shells. Delete the two tampered sibling folders.
