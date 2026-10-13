# =====================================================================
#  Funnel - one-click build
#  Produces a standalone single-file Funnel.exe in .\dist
#  Requires: the .NET 8 SDK installed on this machine (installer from
#            https://dotnet.microsoft.com/download/dotnet/8.0)
# =====================================================================
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here "src\Funnel.csproj"
$dist = Join-Path $here "dist"

# PATH first, then the installer's standard locations.
function Find-Dotnet {
  $d = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
  if ($d) { return $d }
  foreach ($c in @("$env:ProgramFiles\dotnet\dotnet.exe",
                   "${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
                   "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
    if ($c -and (Test-Path $c)) { return $c }
  }
  return $null
}
$dotnet = Find-Dotnet
if (-not $dotnet) {
  Write-Error "No .NET SDK found on this machine. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 then re-run build.ps1."
}
if (-not ((& $dotnet --list-sdks | Out-String) -match "^\d")) {
  Write-Error "'$dotnet' is a .NET runtime, not an SDK. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 then re-run build.ps1."
}
Write-Host ("Using .NET SDK: " + $dotnet) -ForegroundColor DarkGray

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
Write-Host "Building Funnel (self-contained single file)..." -ForegroundColor Cyan
& $dotnet publish $proj -c Release -r win-x64 -o $dist
# A failed publish leaves the PREVIOUS build in dist, so check the exit code, not the file.
if ($LASTEXITCODE -ne 0) {
  if (Get-Process Funnel -ErrorAction SilentlyContinue) {
    Write-Host ""
    Write-Host "Funnel is running, so dist\Funnel.exe can't be overwritten. Close it and re-run." -ForegroundColor Yellow
  }
  Write-Error "Publish failed (exit code $LASTEXITCODE). dist still holds the previous build."
}

Get-ChildItem $dist -Filter *.xml | Remove-Item -Force -ErrorAction SilentlyContinue

$exe = Join-Path $dist "Funnel.exe"
if (Test-Path $exe) {
  $mb = [math]::Round((Get-Item $exe).Length / 1MB, 2)
  $sha = (Get-FileHash $exe -Algorithm SHA256).Hash
  Write-Host ""
  Write-Host "BUILD OK -> $exe ($mb MB)" -ForegroundColor Green
  Write-Host "SHA-256 : $sha" -ForegroundColor Green
} else {
  Write-Error "Build finished but Funnel.exe was not found in $dist"
}
