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
| Doppelklick | zurück zur Liste |
| Mittlere Maustaste (oder F11) | Vollbild ein/aus |
| Esc | zurück zur Liste (beendet auch das Vollbild) |
| Rechtsklick | Verschieben …, Kopieren …, Sortieren, Lade-Modus |

Das Fenster wächst beim Zoomen mit (bis zum Arbeitsbereich des Monitors). Lade-Modi (Rechtsklick): *Optimale Größe*
(einpassen, nur verkleinern), *Aktuelle Zoomstufe*, *Originalgröße* (Standard nach Programmstart).
Verschieben/Kopieren: Liste mit bis zu 10 Zielordnern (Hinzufügen, Löschen, Sortieren, ▲▼); Doppelklick oder OK führt aus.
Sortierung: Geändert / Erstellt / Name, je auf- oder absteigend; Name nach Windows-Art (ABC_1, ABC_10, ABC_100).

## Icons (Ordner `src\ICON`)

- `APP_ICON.png` wird von `build.ps1` in eine mehrgrößige `.ico` (16–256 px) umgewandelt und als Anwendungs-Icon der EXE
  gesetzt (Explorer, Taskleiste, Fenster).
- Alle `*.ico` aus `src\ICON` werden von `build.ps1` als Icon-Ressourcen **in die EXE eingebettet**. Welche Dateiendungen
  zu welchem Icon gehören, steht in `src\ICON\icon-map.json` (Name der `.ico` ohne Endung → Liste der Endungen).
  Neues Icon: `XYZ.ico` ablegen, in `icon-map.json` eintragen, `build.ps1` und `register.ps1` erneut ausführen.
- `build.ps1` schreibt die aufgelöste Zuordnung nach `publish\icons.json`. `register.ps1` legt damit pro Icon eine eigene
  ProgID an (`Bildbetrachter.JPG` usw., `DefaultIcon` = `Bildbetrachter.exe,-<ID>`) und ordnet jede Endung zu.
  Endungen ohne eigenes Icon (z. B. HEIC, AVIF) bekommen das Programm-Icon.
- Die Datei-Icons sind aktuell nur 16 × 16 px; für scharfe Darstellung bei großen Symbolen bitte größere Varianten
  (32/48/256 px) in dieselbe `.ico` packen.
- Das Einbetten ändert die fertige EXE nachträglich – deshalb keine „Single-File“-Veröffentlichung verwenden.

## Fenster-Verhalten (ab Version 1.1)

- Die **Listenansicht** hat eine eigene, gespeicherte Fenstergröße. Beim Zurückkehren aus der Bildansicht (und beim Start)
  erscheint sie in dieser Größe mittig auf dem aktuellen Bildschirm.
- Beim Wechsel in die **Bildansicht** passt sich das Fenster der Bildgröße an (höchstens bis zum Arbeitsbereich des
  Monitors); das Bild wird zentriert. Bei weiteren Bildern und beim Zoomen wächst das Fenster nur noch.
- **Spaltenbreiten** (Ordner, Vorschau, Name, Geändert, Größe) werden gespeichert.
- Der **Ordnerbaum** zeigt oben Bilder, Desktop, Dokumente, Downloads, Videos, Musik und Userhome, darunter „Dieser PC“.
  „Galerie“ fehlt bewusst: Das ist in Windows 11 ein virtueller Ordner ohne echten Pfad.
- **Miniaturvorschau (50 × 50)**: Rechtsklick → „Miniaturvorschau“; wird gespeichert. Die Miniaturen werden nur für
  sichtbare Zeilen im Hintergrund geladen.

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
