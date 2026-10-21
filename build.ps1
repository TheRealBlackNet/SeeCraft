<#
  Baut den Bildbetrachter nach .\publish
    .\build.ps1                  -> klein, braucht die .NET 10 Desktop Runtime
    .\build.ps1 -SelfContained   -> alles enthalten (ca. 150 MB), keine Runtime noetig
    .\build.ps1 -Arm64           -> fuer Windows on ARM

  Icons (Ordner src\ICON):
    APP_ICON.png   -> wird in eine mehrgroessige .ico umgewandelt und als Anwendungs-Icon der EXE gesetzt
    *.ico          -> werden als Icon-Ressourcen in die EXE eingebettet (Zuordnung Endung <-> Icon: src\ICON\icon-map.json)
  Das Ergebnis der Zuordnung steht danach in publish\icons.json und wird von register.ps1 gelesen.
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

# ------------------------------------------------------------------ Icon-Werkzeuge
if (-not ([System.Management.Automation.PSTypeName]'IconTools').Type) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class IconTools
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr BeginUpdateResource(string fileName, bool deleteExisting);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateResource(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EndUpdateResource(IntPtr h, bool discard);

    const int RT_ICON = 3;
    const int RT_GROUP_ICON = 14;

    // Bettet .ico-Dateien als Win32-Icon-Ressourcen in eine EXE ein.
    // groupIds[i] ist die Ressourcen-ID der i-ten Icon-Gruppe (Verweis spaeter als "exe,-ID").
    // Die Gruppe mit der kleinsten ID ist das Anwendungs-Icon (Explorer, Taskleiste).
    public static void Embed(string exePath, string[] icoPaths, int[] groupIds)
    {
        IntPtr h = BeginUpdateResource(exePath, false);
        if (h == IntPtr.Zero)
            throw new IOException("BeginUpdateResource fehlgeschlagen (Win32-Fehler " + Marshal.GetLastWin32Error() + "). Laeuft die EXE noch?");
        bool ok = false;
        try
        {
            int nextIconId = 1000;
            for (int i = 0; i < icoPaths.Length; i++)
            {
                byte[] ico = File.ReadAllBytes(icoPaths[i]);
                if (ico.Length < 22 || BitConverter.ToUInt16(ico, 2) != 1)
                    throw new InvalidDataException("Keine gueltige .ico-Datei: " + icoPaths[i]);
                int count = BitConverter.ToUInt16(ico, 4);

                MemoryStream grp = new MemoryStream();
                BinaryWriter w = new BinaryWriter(grp);
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)count);

                for (int k = 0; k < count; k++)
                {
                    int e = 6 + 16 * k;
                    int size = BitConverter.ToInt32(ico, e + 8);
                    int offset = BitConverter.ToInt32(ico, e + 12);
                    byte[] img = new byte[size];
                    Array.Copy(ico, offset, img, 0, size);

                    int id = nextIconId++;
                    if (!UpdateResource(h, (IntPtr)RT_ICON, (IntPtr)id, 0, img, (uint)img.Length))
                        throw new IOException("UpdateResource (RT_ICON) fehlgeschlagen: " + Marshal.GetLastWin32Error());

                    w.Write(ico[e]);                          // Breite
                    w.Write(ico[e + 1]);                      // Hoehe
                    w.Write(ico[e + 2]);                      // Farbanzahl
                    w.Write(ico[e + 3]);                      // reserviert
                    w.Write(BitConverter.ToUInt16(ico, e + 4)); // Planes
                    w.Write(BitConverter.ToUInt16(ico, e + 6)); // Bits pro Pixel
                    w.Write((uint)size);
                    w.Write((ushort)id);
                }
                w.Flush();
                byte[] g = grp.ToArray();
                if (!UpdateResource(h, (IntPtr)RT_GROUP_ICON, (IntPtr)groupIds[i], 0, g, (uint)g.Length))
                    throw new IOException("UpdateResource (RT_GROUP_ICON) fehlgeschlagen: " + Marshal.GetLastWin32Error());
            }
            ok = true;
        }
        finally
        {
            EndUpdateResource(h, !ok);
        }
    }
}
'@
}

function New-IcoFromPng([string]$Png, [string]$Ico) {
    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile($Png)
    $frames = New-Object System.Collections.ArrayList
    try {
        foreach ($s in 16, 20, 24, 32, 40, 48, 64, 96, 128, 256) {
            $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)

            $scale = [Math]::Min($s / $src.Width, $s / $src.Height)
            $w = [int][Math]::Round($src.Width * $scale)
            $h = [int][Math]::Round($src.Height * $scale)
            $x = [int][Math]::Floor(($s - $w) / 2)
            $y = [int][Math]::Floor(($s - $h) / 2)
            $ia = New-Object System.Drawing.Imaging.ImageAttributes
            $ia.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $dest = New-Object System.Drawing.Rectangle($x, $y, $w, $h)
            $g.DrawImage($src, $dest, 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
            $g.Dispose()

            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            [void]$frames.Add(@{ Size = $s; Data = $ms.ToArray() })
        }
    } finally { $src.Dispose() }

    $fs = [System.IO.File]::Create($Ico)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($f in $frames) {
            $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
            $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
            $bw.Write([uint16]1); $bw.Write([uint16]32)
            $bw.Write([uint32]$f.Data.Length); $bw.Write([uint32]$offset)
            $offset += $f.Data.Length
        }
        foreach ($f in $frames) { $bw.Write([byte[]]$f.Data) }
    } finally { $bw.Dispose(); $fs.Dispose() }
}

# ------------------------------------------------------------------ Icons vorbereiten
$iconDir  = Join-Path $PSScriptRoot 'src\ICON'
$buildDir = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force $buildDir | Out-Null

# 1) Programm-Icon: APP_ICON.png -> build\app.ico
$appPng = Join-Path $iconDir 'APP_ICON.png'
$appIco = Join-Path $buildDir 'app.ico'
if (Test-Path $appPng) {
    New-IcoFromPng $appPng $appIco
    Write-Host "Programm-Icon erzeugt: $appIco"
} else {
    Write-Host "Warnung: $appPng fehlt - die EXE bekommt kein eigenes Programm-Icon." -ForegroundColor Yellow
    $appIco = $null
}

# 2) Datei-Icons laut src\ICON\icon-map.json
$mapFile = Join-Path $iconDir 'icon-map.json'
$fileIcons = @()      # fuer icons.json
$embedPaths = @()
$embedIds = @()
if ($appIco) { $embedPaths += $appIco; $embedIds += 1 }     # Gruppe 1 = kleinste ID = Anwendungs-Icon
if (Test-Path $mapFile) {
    $map = Get-Content $mapFile -Raw | ConvertFrom-Json
    $id = 100
    foreach ($p in $map.PSObject.Properties) {
        $id++
        $ico = Join-Path $iconDir ($p.Name + '.ico')
        if (-not (Test-Path $ico)) {
            Write-Host "Warnung: $ico fehlt - Endungen $(@($p.Value) -join ', ') behalten das Standard-Icon." -ForegroundColor Yellow
            continue
        }
        $embedPaths += $ico
        $embedIds += $id
        $fileIcons += [pscustomobject]@{ name = $p.Name; id = $id; extensions = @($p.Value) }
    }
} else {
    Write-Host "Hinweis: $mapFile nicht gefunden - keine Datei-Icons." -ForegroundColor Yellow
}

# ------------------------------------------------------------------ Publish
$rid = if ($Arm64) { 'win-arm64' } else { 'win-x64' }
$sc  = if ($SelfContained) { 'true' } else { 'false' }
$out = Join-Path $PSScriptRoot 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$extra = @()
if ($appIco) { $extra += "-p:ApplicationIcon=$appIco" }

dotnet publish .\src\Bildbetrachter\Bildbetrachter.csproj -c Release -r $rid `
    -p:SelfContained=$sc -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false @extra -o $out
if ($LASTEXITCODE -ne 0) { Write-Host "Build fehlgeschlagen." -ForegroundColor Red; exit $LASTEXITCODE }

# ------------------------------------------------------------------ Icons in die EXE einbetten
$exe = Join-Path $out 'Bildbetrachter.exe'
if ($embedPaths.Count -gt 0) {
    [IconTools]::Embed($exe, [string[]]$embedPaths, [int[]]$embedIds)
    Write-Host "In die EXE eingebettet: $($embedPaths.Count) Icon-Gruppe(n)."
}
ConvertTo-Json -InputObject @($fileIcons) -Depth 4 | Set-Content -Path (Join-Path $out 'icons.json') -Encoding UTF8

Write-Host ""
Write-Host "Fertig: $exe" -ForegroundColor Green
if (-not $SelfContained) {
    Write-Host "Hinweis: Benoetigt die .NET 10 Desktop Runtime:  winget install Microsoft.DotNet.DesktopRuntime.10"
}
Write-Host "Naechster Schritt:  .\register.ps1   (installiert und macht das Programm in Windows auswaehlbar)"
