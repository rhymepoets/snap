# Builds Snap.exe into %LOCALAPPDATA%\Snap, starts it with Windows, and launches it.
#   powershell -ExecutionPolicy Bypass -File install.ps1             install or update
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall  remove it
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Snap'
$exe = Join-Path $dest 'Snap.exe'
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'Snap.lnk'

Get-Process Snap -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

if ($Uninstall) {
    Remove-Item $lnk -ErrorAction SilentlyContinue
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output "Removed Snap. Your screenshots stay in Pictures\Screenshots."
    return
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc (it ships with .NET Framework 4, part of Windows 10/11)" }

New-Item -ItemType Directory -Force $dest | Out-Null
& $csc /nologo /optimize+ /target:winexe /out:$exe /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'Snap.cs')
if ($LASTEXITCODE -ne 0) { throw "compile failed" }

$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $dest
$s.Description = 'Snap: tap Ctrl+Shift to screenshot'
$s.Save()

Start-Process $exe
Write-Output "Snap is running (tray icon) and starts with Windows. Tap Ctrl+Shift to try it."
