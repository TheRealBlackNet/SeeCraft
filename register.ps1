<#
  Installiert den Bildbetrachter nach %LOCALAPPDATA%\Programs\Bildbetrachter und registriert ihn
  fuer Bilddateien (nur fuer den aktuellen Benutzer, keine Admin-Rechte noetig).
  Jede Dateiendung bekommt ihr eigenes Icon (aus der EXE, Zuordnung laut publish\icons.json,
  die build.ps1 aus src\ICON\icon-map.json erzeugt). Endungen ohne eigenes Icon bekommen das Programm-Icon.
    .\register.ps1              -> installieren/aktualisieren
    .\register.ps1 -Uninstall   -> alles wieder entfernen
  Hinweis: Windows 10/11 erlaubt es Programmen nicht, sich selbst als Standard-App zu setzen.
  Das Skript oeffnet deshalb am Ende die Windows-Einstellungen, dort einmal "Bildbetrachter" waehlen.
#>
param(
    [switch]$Uninstall,
    [string]$SourceDir = (Join-Path $PSScriptRoot 'SeeCraft')
)
$ErrorActionPreference = 'Stop'

$app        = 'Bildbetrachter'
$genericId  = "$app.Image"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$app"
$exe        = Join-Path $installDir "$app.exe"

# alle Endungen, die der Bildbetrachter oeffnen kann
$allExts = '.jpg','.jpeg','.jpe','.jfif','.png','.gif','.bmp','.dib','.tif','.tiff','.ico',
           '.webp','.heic','.heif','.avif','.jxl','.tga','.psd','.pcx','.emf','.wmf'

Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);'
function Refresh-Shell {
    [Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    try { & "$env:windir\System32\ie4uinit.exe" -show } catch { }   # Icon-Cache auffrischen
}

if ($Uninstall) {
    Get-ChildItem 'HKCU:\Software\Classes' | Where-Object { $_.PSChildName -like "$app.*" } |
        ForEach-Object { Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item "HKCU:\Software\$app" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty "HKCU:\Software\RegisteredApplications" -Name $app -ErrorAction SilentlyContinue
    foreach ($e in $allExts) {
        $k = "HKCU:\Software\Classes\$e\OpenWithProgids"
        if (Test-Path $k) {
            (Get-Item $k).Property | Where-Object { $_ -like "$app.*" } |
                ForEach-Object { Remove-ItemProperty $k -Name $_ -ErrorAction SilentlyContinue }
        }
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

# 2) Zuordnung Icon <-> Endung lesen (Icon-ID = Ressourcen-ID in der EXE)
$iconFile = Join-Path $installDir 'icons.json'
$icons = @()
if (Test-Path $iconFile) {
    $parsed = Get-Content $iconFile -Raw | ConvertFrom-Json
    $icons = @($parsed | ForEach-Object { $_ } | Where-Object { $_ })   # Array sicher entpacken (PS 5.1 und 7)
}

$assoc = @{}   # Endung -> ProgID
function Register-ProgId([string]$ProgId, [string]$Description, [string]$IconSpec) {
    $cls = "HKCU:\Software\Classes\$ProgId"
    New-Item "$cls\shell\open\command" -Force | Out-Null
    New-Item "$cls\DefaultIcon" -Force | Out-Null
    Set-ItemProperty $cls -Name '(default)' -Value $Description
    Set-ItemProperty "$cls\DefaultIcon" -Name '(default)' -Value $IconSpec
    Set-ItemProperty "$cls\shell\open\command" -Name '(default)' -Value "`"$exe`" `"%1`""
}

# 2a) allgemeine ProgID mit Programm-Icon (Gruppe 1 in der EXE)
Register-ProgId $genericId 'Bilddatei (Bildbetrachter)' "`"$exe`",-1"
foreach ($e in $allExts) { $assoc[$e] = $genericId }

# 2b) eigene ProgID + Icon je Eintrag aus icons.json (z. B. JPG -> .jpg .jpeg .jpe .jfif)
foreach ($ic in $icons) {
    $progId = "$app.$($ic.name)"
    Register-ProgId $progId "$($ic.name)-Bild (Bildbetrachter)" "`"$exe`",-$($ic.id)"
    foreach ($e in @($ic.extensions)) { $assoc[$e.ToLowerInvariant()] = $progId }
}

# 3) Capabilities (erscheint unter Einstellungen > Standard-Apps) + "Oeffnen mit"-Eintraege
$cap = "HKCU:\Software\$app\Capabilities"
New-Item "$cap\FileAssociations" -Force | Out-Null
Set-ItemProperty $cap -Name ApplicationName -Value $app
Set-ItemProperty $cap -Name ApplicationDescription -Value 'Schneller Bildbetrachter mit Bild- und Listenansicht'
foreach ($e in $assoc.Keys) {
    Set-ItemProperty "$cap\FileAssociations" -Name $e -Value $assoc[$e]
    $owp = "HKCU:\Software\Classes\$e\OpenWithProgids"
    New-Item $owp -Force | Out-Null
    # evtl. alte Bildbetrachter-ProgIDs dieser Endung entfernen, dann die aktuelle eintragen
    (Get-Item $owp).Property | Where-Object { $_ -like "$app.*" } |
        ForEach-Object { Remove-ItemProperty $owp -Name $_ -ErrorAction SilentlyContinue }
    New-ItemProperty $owp -Name $assoc[$e] -PropertyType None -Value ([byte[]]@()) -Force | Out-Null
}
New-Item 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name $app -Value "Software\$app\Capabilities"

Refresh-Shell
Write-Host "Installiert nach: $installDir" -ForegroundColor Green
Write-Host "Icons zugeordnet fuer: $((($assoc.Keys | Sort-Object) -join ' '))"
Write-Host ""
Write-Host "Letzter Schritt (kann Windows nicht automatisch):"
Write-Host "  Einstellungen > Apps > Standard-Apps > 'Bildbetrachter' waehlen > 'Als Standard festlegen'"
Write-Host "  (bzw. pro Dateityp .jpg/.png/.gif ... den Bildbetrachter eintragen)"
Start-Process 'ms-settings:defaultapps'
