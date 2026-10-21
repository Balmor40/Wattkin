# Hardware self-test for the MSI Stealth 15M A11UEK (EC 1563EMS1.115): first read-only checks, then
# (after you confirm) write tests that change one setting at a time and put everything back.
#   .\build.ps1                 # builds .\dist\Wattkin.exe
#   .\tools\selftest.ps1        # read, then write after a prompt (-Stage read|write|all)
# Stop YAMDCC first (Stop-Service yamdccsvc) and close any running Wattkin: two programs writing the
# embedded controller at once would spoil the results. Asks for administrator rights once (UAC).
# Reports go to %LOCALAPPDATA%\Wattkin\selftest; every controller write is in %LOCALAPPDATA%\Wattkin\logs\wattkin.log.
param(
    [ValidateSet('read', 'write', 'all')][string]$Stage = 'all',
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\Wattkin.exe'),
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
$Exe = [IO.Path]::GetFullPath($Exe)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $child = Start-Process powershell.exe -Verb RunAs -PassThru -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Stage $Stage -Exe `"$Exe`" -Elevated"
    $child.WaitForExit()
    exit $child.ExitCode
}

function Finish([int]$Code) {
    if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
    exit $Code
}

if (-not (Test-Path $Exe)) {
    Write-Host "No build found at $Exe. Run .\build.ps1 first." -ForegroundColor Red
    Finish 2
}

$yamdcc = Get-Service yamdccsvc -ErrorAction SilentlyContinue
if ($yamdcc -and $yamdcc.Status -ne 'Stopped') {
    Write-Host 'The YAMDCC service is running. Stop it first (and keep it from starting with Windows):' -ForegroundColor Red
    Write-Host '  Stop-Service yamdccsvc; Set-Service yamdccsvc -StartupType Manual'
    Finish 2
}

if (Get-Process Wattkin -ErrorAction SilentlyContinue) {
    Write-Host 'Wattkin is running. Close it first (tray icon > Exit, or "Wattkin.exe --exit").' -ForegroundColor Red
    Finish 2
}

$reports = Join-Path $env:LOCALAPPDATA 'Wattkin\selftest'
New-Item -ItemType Directory -Force $reports | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Run-Stage([string]$Name) {
    $report = Join-Path $reports "selftest-$Name-$stamp.txt"
    Write-Host ''
    Write-Host "Running the $Name tests..." -ForegroundColor Cyan
    $p = Start-Process $Exe -ArgumentList "--selftest $Name `"$report`"" -Wait -PassThru
    if (Test-Path $report) {
        foreach ($line in Get-Content $report -Encoding UTF8) {
            $color = switch -Regex ($line) { '^PASS' { 'Green' } '^FAIL' { 'Red' } '^(WARN|SKIP)' { 'Yellow' } '^==' { 'Cyan' } default { 'Gray' } }
            Write-Host $line -ForegroundColor $color
        }
    }
    Write-Host ''
    Write-Host "Report: $report"
    return $p.ExitCode
}

$code = 0
if ($Stage -in 'read', 'all') {
    $code = Run-Stage 'read'
    if ($code -eq 2) { Finish 2 }
}

if ($Stage -in 'write', 'all') {
    if ($Stage -eq 'all') {
        Write-Host ''
        Write-Host 'The write tests change one setting at a time (battery limit, performance and fan modes, keyboard'
        Write-Host 'light, mute lights, webcam, Cooler Boost, running from the battery, fan tables) and put each one back.'
        Write-Host 'Keep the charger connected and the laptop idle: it takes about 4 minutes, and the fans will be loud for a moment.'
        if ((Read-Host 'Run the write tests? [y/N]') -notmatch '^(y|yes|o|oui)$') { Finish $code }
    }

    $write = Run-Stage 'write'
    $code = [Math]::Max($code, $write)
}

Finish $code
