# Bildbetrachter – Ersatz für ACDSee 3.1

Schneller Bildbetrachter für Windows 10/11 mit zwei Ansichten (**Liste** und **Bild**).
C# / .NET 10 / WPF. **Hinweis:** Der Quelltext wurde noch nicht auf einem Windows-Rechner kompiliert – falls der
Build Fehler meldet, bitte die Meldung zurückschicken, dann wird nachgebessert.

## Installation in 3 Schritten

Normale PowerShell im Ordner dieses Projekts öffnen:

```powershell
winget install Microsoft.DotNet.SDK.10        # einmalig: Compiler
.\build.ps1                                   # baut nach .\publish   (Option: -SelfContained)
.\register.ps1                                # installiert nach %LOCALAPPDATA%\Programs\Bildbetrachter
```

Danach öffnet sich *Einstellungen → Standard-Apps*. Dort **Bildbetrachter** wählen und „Als Standard festlegen“
(Windows erlaubt es Programmen nicht, sich selbst zum Standard zu machen). Entfernen: `.\register.ps1 -Uninstall`.

Läuft der Build ohne `-SelfContained`, braucht das Programm die Runtime:
`winget install Microsoft.DotNet.DesktopRuntime.10`.

## Bedienung

**Allgemein:** Beim Start wird der zuletzt benutzte Ordner in der Listenansicht geöffnet (existiert er nicht mehr:
Bilder-Ordner, sonst Dokumente/Desktop). Wird eine Bilddatei per Doppelklick/„Öffnen mit“ gestartet, öffnet sich
direkt die Bildansicht.

**Liste** (Ordnerbaum | Vorschau | Bildliste, Spaltenbreiten per Trenner einstellbar und gespeichert)
- Doppelklick oder Enter → Bildansicht, F5 → Ordner neu einlesen
- Rechtsklick: Verschieben … / Kopieren … / Sortieren / Bilder beim Laden anzeigen / Bildinformationen
- Bildinformationen: Format, Breite×Höhe, Pixelzahl, Farbstufen, Dateigröße in Byte; bei PNGs mit Text-Metadaten
  (z. B. Stable-Diffusion-Prompt) erscheint in der Vorschau ein ⓘ und ein Kopieren-Button

**Bild**
| Aktion | Wirkung |
|---|---|
| Ziehen mit linker Maustaste | Ist das Bild kleiner als das Fenster: Rechteck aufziehen → auf den Bereich zoomen. Ist es größer: Bildausschnitt verschieben. Mit **Shift** immer Zoom-Rechteck |
| Mausrad | nächstes / vorheriges Bild (mit **Strg**: Zoom) |
| Bild ↑ / Bild ↓ | vorheriges / nächstes Bild |
| Pos1 / Ende | erstes / letztes Bild der aktuellen Sortierung |
| Pfeiltasten | Bildausschnitt verschieben |
| Num + / Num − | Zoom: 10 20 30 50 70 100 125 150 200 300 500 700 1000 2000 3000 5000 % (zwischen zwei Stufen → nächste Stufe) |
| Esc | zurück zur Liste |
| Rechtsklick | Verschieben …, Kopieren …, Sortieren, Lade-Modus |

Das Fenster wächst beim Zoomen mit (bis zum Arbeitsbereich des Monitors). Lade-Modi (Rechtsklick): *Optimale Größe*
(einpassen, nur verkleinern), *Aktuelle Zoomstufe*, *Originalgröße* (Standard nach Programmstart).
Verschieben/Kopieren: Liste mit bis zu 10 Zielordnern (Hinzufügen, Löschen, Sortieren, ▲▼); Doppelklick oder OK führt aus.
Sortierung: Geändert / Erstellt / Name, je auf- oder absteigend; Name nach Windows-Art (ABC_1, ABC_10, ABC_100).

## Technik & Lizenzen

| Baustein | Zweck | Lizenz |
|---|---|---|
| .NET 10 + WPF | Laufzeit, Oberfläche, JPG/PNG/GIF/BMP/TIFF/ICO über Windows-Codecs (WIC) | MIT |
| Magick.NET-Q8-AnyCPU (ImageMagick) | WebP, HEIC/HEIF, AVIF, JPEG XL, TGA, PSD, JPEG 2000, DDS, PCX | Apache-2.0 (ImageMagick-Lizenz, Apache-ähnlich) |
| WpfAnimatedGif | animierte GIFs | Apache-2.0 |

Alle drei erlauben private und kommerzielle Nutzung ohne Gebühren; bei Weitergabe der Binärdateien die jeweiligen
Lizenztexte beilegen. Die in Magick.NET enthaltenen HEIC/AVIF-Decoder (libheif u. a.) haben eigene Lizenzen
(teils LGPL) – für die private Nutzung unproblematisch, bei Weitergabe prüfen.

**Geschwindigkeit & Garbage Collector:** Dekodierung läuft im Hintergrund (max. 2 parallel), veraltete Anfragen werden beim
schnellen Blättern abgebrochen, die Nachbarbilder werden vorgeladen, Bilder liegen in einem LRU-Cache mit festem
Speicherbudget (600 MB Vollbilder / 150 MB Vorschauen; Konstanten in `ImageService.cs`). Dekodierte Bitmaps liegen bei
WIC im nativen Speicher, nicht im verwalteten Heap; zusätzlich: Workstation-GC mit Concurrent-GC und
`SustainedLowLatency`.

## Anpassen

- Spaltenreihenfolge in der Liste: `MainWindow.xaml`
- Vorschau nicht über 100 % vergrößern: `StretchDirection="DownOnly"` beim `PreviewImage`
- Weitere Dateiendungen: `ImageFiles.cs`
- Einstellungen liegen in `%APPDATA%\Bildbetrachter\settings.json`
