<#
  Installiert den Bildbetrachter nach %LOCALAPPDATA%\Programs\Bildbetrachter und registriert ihn
  fuer Bilddateien (nur fuer den aktuellen Benutzer, keine Admin-Rechte noetig).
    .\register.ps1              -> installieren/aktualisieren
    .\register.ps1 -Uninstall   -> alles wieder entfernen
  Hinweis: Windows 10/11 erlaubt es Programmen nicht, sich selbst als Standard-App zu setzen.
  Das Skript oeffnet deshalb am Ende die Windows-Einstellungen, dort einmal "Bildbetrachter" waehlen.
#>
param(
    [switch]$Uninstall,
    [string]$SourceDir = (Join-Path $PSScriptRoot 'publish')
)
$ErrorActionPreference = 'Stop'

$app        = 'Bildbetrachter'
$progId     = "$app.Image"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$app"
$exe        = Join-Path $installDir "$app.exe"
$exts       = '.jpg','.jpeg','.jpe','.jfif','.png','.gif','.bmp','.tif','.tiff','.ico','.webp','.heic','.heif','.avif','.jxl','.tga','.psd'

Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);'
function Refresh-Shell { [Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) }

if ($Uninstall) {
    Remove-Item "HKCU:\Software\Classes\$progId" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\$app" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty "HKCU:\Software\RegisteredApplications" -Name $app -ErrorAction SilentlyContinue
    foreach ($e in $exts) {
        Remove-ItemProperty "HKCU:\Software\Classes\$e\OpenWithProgids" -Name $progId -ErrorAction SilentlyContinue
    }
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Refresh-Shell
    Write-Host "Bildbetrachter wurde entfernt (Einstellungen in %APPDATA%\Bildbetrachter bleiben erhalten)."
    return
}

if (-not (Test-Path (Join-Path $SourceDir "$app.exe"))) {
    Write-Host "Keine gebauten Dateien in '$SourceDir' gefunden. Zuerst .\build.ps1 ausfuehren." -ForegroundColor Yellow
    exit 1
}

# 1) Dateien kopieren
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item (Join-Path $SourceDir '*') $installDir -Recurse -Force

# 2) ProgID (wie eine Bilddatei geoeffnet wird)
$cls = "HKCU:\Software\Classes\$progId"
New-Item "$cls\shell\open\command" -Force | Out-Null
New-Item "$cls\DefaultIcon" -Force | Out-Null
Set-ItemProperty $cls -Name '(default)' -Value 'Bilddatei (Bildbetrachter)'
Set-ItemProperty "$cls\DefaultIcon" -Name '(default)' -Value "`"$exe`",0"
Set-ItemProperty "$cls\shell\open\command" -Name '(default)' -Value "`"$exe`" `"%1`""

# 3) Capabilities (erscheint unter Einstellungen > Standard-Apps)
$cap = "HKCU:\Software\$app\Capabilities"
New-Item "$cap\FileAssociations" -Force | Out-Null
Set-ItemProperty $cap -Name ApplicationName -Value $app
Set-ItemProperty $cap -Name ApplicationDescription -Value 'Schneller Bildbetrachter mit Bild- und Listenansicht'
foreach ($e in $exts) {
    Set-ItemProperty "$cap\FileAssociations" -Name $e -Value $progId
    New-Item "HKCU:\Software\Classes\$e\OpenWithProgids" -Force | Out-Null
    New-ItemProperty "HKCU:\Software\Classes\$e\OpenWithProgids" -Name $progId -PropertyType None -Value ([byte[]]@()) -Force | Out-Null
}
New-Item 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name $app -Value "Software\$app\Capabilities"

Refresh-Shell
Write-Host "Installiert nach: $installDir" -ForegroundColor Green
Write-Host ""
Write-Host "Letzter Schritt (kann Windows nicht automatisch):"
Write-Host "  Einstellungen > Apps > Standard-Apps > 'Bildbetrachter' waehlen > 'Als Standard festlegen'"
Write-Host "  (bzw. pro Dateityp .jpg/.png/.gif ... den Bildbetrachter eintragen)"
Start-Process 'ms-settings:defaultapps'
