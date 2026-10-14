# Publishes a Wattkin release that installed copies update to:
#   .\tools\release.ps1 0.2.2 -Notes "What changed"
# Sets the version, runs the tests and builds, signs the exe with your release key, commits and tags,
# pushes, then creates the GitHub release with Wattkin.exe and Wattkin.exe.sig.
param([Parameter(Mandatory)][string]$Version, [string]$Notes = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Use a version like 0.2.2." }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk\dotnet.exe'
if (Test-Path $userSdk) { $dotnet = $userSdk; $env:DOTNET_ROOT = Split-Path $userSdk }

# Runs a program and fails on its exit code only: Windows PowerShell would otherwise treat any line
# on stderr (git's line-ending warnings, for one) as a fatal error.
function Run([string]$Exe) {
    $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $Exe @args 2>&1 | ForEach-Object { "$_" } | Out-Host } finally { $ErrorActionPreference = $saved }
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($args -join ' ') failed (exit code $LASTEXITCODE)." }
}

Push-Location $root
try {
    if (git status --porcelain) { throw 'Commit or stash your changes first: a release is built from a clean tree.' }
    if (git tag --list "v$Version") { throw "v$Version already exists." }

    $csproj = Join-Path $root 'src\Wattkin\Wattkin.csproj'
    $text = [IO.File]::ReadAllText($csproj)
    $text = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
    [IO.File]::WriteAllText($csproj, $text, (New-Object Text.UTF8Encoding $false))

    & (Join-Path $root 'build.ps1')
    $exe = Join-Path $root 'dist\Wattkin.exe'
    Run $dotnet run --project (Join-Path $PSScriptRoot 'Release') -c Release -- sign $exe $Version

    if (git status --porcelain $csproj) {
        Run git add $csproj
        Run git commit -q -m "Release v$Version"
    }
    Run git tag -a "v$Version" -m "Wattkin $Version"
    Run git push -q --follow-tags origin HEAD

    $notesFile = [IO.Path]::GetTempFileName()
    [IO.File]::WriteAllText($notesFile, $Notes)
    try { Run $dotnet run --project (Join-Path $PSScriptRoot 'Release') -c Release -- publish $Version $exe $notesFile }
    finally { Remove-Item $notesFile }
} finally {
    Pop-Location
}
