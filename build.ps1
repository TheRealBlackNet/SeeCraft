<#
  Baut den Bildbetrachter nach .\publish
    .\build.ps1                  -> klein, braucht die .NET 10 Desktop Runtime
    .\build.ps1 -SelfContained   -> alles enthalten (ca. 150 MB), keine Runtime noetig
    .\build.ps1 -Arm64           -> fuer Windows on ARM
#>
param(
    [switch]$SelfContained,
    [switch]$Arm64
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "Das .NET SDK fehlt. Installation (einmalig, in einer normalen PowerShell):" -ForegroundColor Yellow
    Write-Host "    winget install Microsoft.DotNet.SDK.10"
    Write-Host "Danach PowerShell neu starten und build.ps1 erneut ausfuehren."
    exit 1
}
if (-not ((dotnet --list-sdks) -match '^10\.')) {
    Write-Host "Es ist kein .NET-10-SDK installiert:  winget install Microsoft.DotNet.SDK.10" -ForegroundColor Yellow
    exit 1
}

$rid = if ($Arm64) { 'win-arm64' } else { 'win-x64' }
$sc  = if ($SelfContained) { 'true' } else { 'false' }
$out = Join-Path $PSScriptRoot 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish .\src\Bildbetrachter\Bildbetrachter.csproj -c Release -r $rid `
    -p:SelfContained=$sc -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { Write-Host "Build fehlgeschlagen." -ForegroundColor Red; exit $LASTEXITCODE }

Write-Host ""
Write-Host "Fertig: $out\Bildbetrachter.exe" -ForegroundColor Green
if (-not $SelfContained) {
    Write-Host "Hinweis: Benoetigt die .NET 10 Desktop Runtime:  winget install Microsoft.DotNet.DesktopRuntime.10"
}
Write-Host "Naechster Schritt:  .\register.ps1   (installiert und macht das Programm in Windows auswaehlbar)"
