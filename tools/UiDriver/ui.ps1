# UI driver for YRK PDF manual testing. Dot-source, then call functions. Usage: . .\ui.ps1
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
}
"@
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W2 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
}
"@
[W]::SetProcessDPIAware() | Out-Null
$global:Shots = Join-Path $PSScriptRoot 'shots'
New-Item -ItemType Directory -Force $global:Shots | Out-Null
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Get-Win {
  $p = Get-Process YrkEasyPdf -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
  if (-not $p) { return $null }
  return $AE::FromHandle($p.MainWindowHandle)
}
function Front { $w = Get-Win; [W]::ShowWindow([IntPtr]$w.Current.NativeWindowHandle, 9) | Out-Null; [W]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null; Start-Sleep -Milliseconds 300 }
function Shot([string]$name, $el = $null) {
  if (-not $el) { $el = Get-Win }
  $r = $el.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
  $path = Join-Path $global:Shots "$name.png"; $bmp.Save($path); $g.Dispose(); $bmp.Dispose(); $path
}
function Find([string]$name, $root = $null, [int]$timeout = 5000) {
  if (-not $root) { $root = $AE::RootElement }
  $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty, $name)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $timeout) {
    $e = $root.FindFirst($TS::Descendants, $cond); if ($e) { return $e }
    Start-Sleep -Milliseconds 200
  }
  throw "Not found: $name"
}
function FindAll([string]$name, $root = $null) {
  if (-not $root) { $root = Get-Win }
  $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty, $name)
  $root.FindAll($TS::Descendants, $cond)
}
function Click-El($el) {
  $p = $null
  if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); Start-Sleep -Milliseconds 400; return }
  if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); Start-Sleep -Milliseconds 400; return }
  if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); Start-Sleep -Milliseconds 400; return }
  $r = $el.Current.BoundingRectangle; Click ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
}
function Guard {
  $fg = [W2]::GetForegroundWindow(); $fpid = 0; [W2]::GetWindowThreadProcessId($fg, [ref]$fpid) | Out-Null
  for ($i = 0; $i -lt 3 -and $fpid -ne (Get-Process YrkEasyPdf).Id; $i++) {
    $h = [IntPtr](Get-Win).Current.NativeWindowHandle
    [K]::keybd_event(0x12,0,0,[UIntPtr]::Zero); [K]::keybd_event(0x12,0,2,[UIntPtr]::Zero)
    [W]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 400
    $fg = [W2]::GetForegroundWindow(); [W2]::GetWindowThreadProcessId($fg, [ref]$fpid) | Out-Null
  }
  $fg = [W2]::GetForegroundWindow(); $fpid = 0; [W2]::GetWindowThreadProcessId($fg, [ref]$fpid) | Out-Null
  $app = (Get-Process YrkEasyPdf).Id
  if ($fpid -ne $app) { throw "YRK PDF is not in front (pid $fpid); input not sent" }
}
function InWin([int]$x, [int]$y) { $r = (Get-Win).Current.BoundingRectangle; if ($x -lt $r.X+4 -or $y -lt $r.Y+4 -or $x -gt $r.Right-12 -or $y -gt $r.Bottom-12) { throw "Point $x,$y is outside the YRK window" } }
function Click([int]$x, [int]$y) { InWin $x $y; Guard; [W]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 80; [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 400 }
function Drag([int]$x1, [int]$y1, [int]$x2, [int]$y2) {
  Guard
  [W]::SetCursorPos($x1, $y1); Start-Sleep -Milliseconds 100
  [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120
  for ($i = 1; $i -le 15; $i++) { [W]::SetCursorPos([int]($x1 + ($x2 - $x1) * $i / 15), [int]($y1 + ($y2 - $y1) * $i / 15)); Start-Sleep -Milliseconds 30 }
  Start-Sleep -Milliseconds 120; [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 500
}
function Keys([string]$k) { Guard; [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 300 }
function TypeText([string]$t) { Guard; foreach ($c in $t.ToCharArray()) { $s = [string]$c; if ('+^%~(){}[]'.Contains($s)) { $s = "{$s}" }; [System.Windows.Forms.SendKeys]::SendWait($s); Start-Sleep -Milliseconds 25 } }
function Rect($el) { $el.Current.BoundingRectangle }
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K { [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra); }
"@
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class U {
  [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk; public ushort scan; public uint flags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit, Size=40)] struct IN { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int size);
  public static void Char(char c) {
    var a = new IN[2];
    a[0].type = 1; a[0].ki.scan = c; a[0].ki.flags = 4;
    a[1].type = 1; a[1].ki.scan = c; a[1].ki.flags = 4 | 2;
    SendInput(2, a, Marshal.SizeOf(typeof(IN)));
  }
}
"@
function IsFront { $fg = [W2]::GetForegroundWindow(); $fpid = 0; [W2]::GetWindowThreadProcessId($fg, [ref]$fpid) | Out-Null; $fpid -eq (Get-Process YrkEasyPdf).Id }
function TypeU([string]$t) { Guard; foreach ($c in $t.ToCharArray()) { if (-not (IsFront)) { throw "Focus left YRK PDF while typing; stopped" }; [U]::Char($c); Start-Sleep -Milliseconds 40 }; Start-Sleep -Milliseconds 300 }
function Key([byte]$vk) { Guard; [K]::keybd_event($vk,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 40; [K]::keybd_event($vk,0,2,[UIntPtr]::Zero); Start-Sleep -Milliseconds 150 }
function Chord([byte]$vk) { Guard; [K]::keybd_event(0x11,0,0,[UIntPtr]::Zero); [K]::keybd_event($vk,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 50; [K]::keybd_event($vk,0,2,[UIntPtr]::Zero); [K]::keybd_event(0x11,0,2,[UIntPtr]::Zero); Start-Sleep 2 }
function Open-App([string]$file) {
  Get-Process YrkEasyPdf -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(10000) | Out-Null }
  $env:YRK_EASYPDF_LOG_DIR = Join-Path $env:TEMP 'yrk-uidriver-logs'
  Start-Process (Join-Path $PSScriptRoot 'uiapp\YrkEasyPdf.exe') -ArgumentList "`"$file`""
  $sw=[Diagnostics.Stopwatch]::StartNew(); while (-not (Get-Win) -and $sw.Elapsed.TotalSeconds -lt 20) { Start-Sleep 1 }
  Start-Sleep 3; $w = Get-Win; [W]::MoveWindow([IntPtr]$w.Current.NativeWindowHandle, 0, 0, 1400, 950, $true) | Out-Null; Start-Sleep 1; Front
}

