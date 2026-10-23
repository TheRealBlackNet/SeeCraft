using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Bildbetrachter;

public partial class MainWindow : Window
{
    const double ListMinW = 520, ListMinH = 320;
    const double ViewerMinW = 200, ViewerMinH = 120;

    readonly AppSettings _s = AppSettings.Load();
    readonly ImageService _svc = new();

    ObservableCollection<ImageItem> _items = new();
    int _index = -1;
    string? _folder;
    int _openToken;
    bool _suppressTree;
    bool _viewerActive;
    int _lastDir = 1;
    ZoomMode _mode = ZoomMode.Original;   // Standard nach Programmstart: Originalgröße

    // Fenstergröße der Listenansicht (unabhängig von der Bildansicht)
    double _listW, _listH;
    bool _listMax;

    // Vollbild
    bool _fullscreen, _fsWasMax;
    Rect _fsBounds;

    readonly TaskCompletionSource _ready = new();   // Start abgeschlossen (für weitergeleitete Pfade)

    bool _enterFit;   // beim Wechsel in die Bildansicht: Fenster auf das erste Bild anpassen

    CancellationTokenSource? _viewCts, _previewCts, _prefetchCts;
    TreeViewItem? _pcNode;
    readonly List<TreeViewItem> _quickNodes = new();
    readonly GridViewColumn _colName, _colModified, _colSize, _colThumb;

    public MainWindow(string? startArg)
    {
        InitializeComponent();

        // Listenfenster: gespeicherte Größe (begrenzt auf den Bildschirm), wird beim Start zentriert
        var wa = SystemParameters.WorkArea;
        _listW = Math.Clamp(_s.Width ?? 1100, ListMinW, wa.Width);
        _listH = Math.Clamp(_s.Height ?? 700, ListMinH, wa.Height);
        _listMax = _s.Maximized;
        Width = _listW;
        Height = _listH;
        if (_listMax) WindowState = WindowState.Maximized;

        ListRoot.ColumnDefinitions[0].Width = new GridLength(Math.Max(120, _s.TreeWidth));
        ListRoot.ColumnDefinitions[2].Width = new GridLength(Math.Max(120, _s.PreviewWidth));

        var gv = (GridView)FileList.View;
        _colName = gv.Columns[0];
        _colModified = gv.Columns[1];
        _colSize = gv.Columns[2];
        _colName.Width = Math.Max(40, _s.ColName);
        _colModified.Width = Math.Max(40, _s.ColModified);
        _colSize.Width = Math.Max(40, _s.ColSize);
        _colThumb = new GridViewColumn { Header = "", Width = 62, CellTemplate = (DataTemplate)FileList.Resources["ThumbCell"] };
        ApplyThumbnailMode();

        FileList.ContextMenu = BuildMenu();
        Viewer.ContextMenu = BuildMenu();
        Viewer.ZoomChanged += byUser =>
        {
            // Zoom durch den Benutzer: Fenster folgt der Bildgröße (größer und kleiner),
            // bei einem Bildwechsel wächst das Fenster nur.
            FitWindowToImage(center: false, allowShrink: byUser, follow: byUser);
            UpdateTitle();
        };
        Viewer.NavigateRequested += d => Step(d);
        Viewer.DoubleClicked += (_, _) => LeaveViewer();
        Viewer.MiddleClicked += (_, _) => ToggleFullscreen();
        ApplyInfoVisibility();

        Loaded += async (_, _) => await StartAsync(startArg);
    }

    // ============================================================ Start / Fenster

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_viewerActive) CaptureListState();
        _s.Width = _listW;
        _s.Height = _listH;
        _s.Maximized = _listMax;
        _s.Save();
    }

    /// <summary>Merkt sich Größe und Spaltenbreiten der Listenansicht (nur aufrufen, solange sie sichtbar ist).</summary>
    void CaptureListState()
    {
        if (WindowState == WindowState.Maximized)
        {
            _listMax = true;
            _listW = RestoreBounds.Width;
            _listH = RestoreBounds.Height;
        }
        else if (WindowState == WindowState.Normal)
        {
            _listMax = false;
            _listW = Width;
            _listH = Height;
        }

        static void Take(double actual, Action<double> set, double min) { if (actual > min) set(actual); }
        Take(ListRoot.ColumnDefinitions[0].ActualWidth, v => _s.TreeWidth = v, 50);
        Take(ListRoot.ColumnDefinitions[2].ActualWidth, v => _s.PreviewWidth = v, 50);
        Take(_colName.ActualWidth, v => _s.ColName = v, 20);
        Take(_colModified.ActualWidth, v => _s.ColModified = v, 20);
        Take(_colSize.ActualWidth, v => _s.ColSize = v, 20);
    }

    void CenterOnWorkArea()
    {
        var wa = Native.GetWorkArea(this);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    /// <summary>Zurück zur gespeicherten Listen-Fenstergröße, mittig auf dem aktuellen Bildschirm.</summary>
    void RestoreListWindow()
    {
        if (_fullscreen) ExitFullscreen(restoreBounds: false);
        MinWidth = ListMinW;
        MinHeight = ListMinH;
        WindowState = WindowState.Normal;
        Width = _listW;
        Height = _listH;
        CenterOnWorkArea();
        if (_listMax) WindowState = WindowState.Maximized;
    }

    async Task StartAsync(string? arg)
    {
        try { await StartCoreAsync(arg); }
        finally { _ready.TrySetResult(); }
    }

    async Task StartCoreAsync(string? arg)
    {
        string? folder = null, file = null;

        if (!string.IsNullOrWhiteSpace(arg))
        {
            arg = arg.Trim('"');
            if (await Task.Run(() => File.Exists(arg)))
            {
                file = Path.GetFullPath(arg);
                folder = Path.GetDirectoryName(file);
            }
            else if (await Task.Run(() => Directory.Exists(arg)))
                folder = Path.GetFullPath(arg);
        }

        // Letzter Ordner - falls nicht mehr verfügbar: Standardordner
        if (folder == null && !string.IsNullOrEmpty(_s.LastFolder)
            && await Task.Run(() => Directory.Exists(_s.LastFolder)))
            folder = _s.LastFolder;

        folder ??= await Task.Run(DefaultFolder);

        InitTree();
        await OpenFolderAsync(folder, file);

        if (file != null) EnterViewer();
    }

    /// <summary>Wird von der zweiten Programminstanz aufgerufen: Bild bzw. Ordner in diesem Fenster öffnen.</summary>
    public async Task OpenExternalAsync(string path)
    {
        try
        {
            await _ready.Task;
            path = path.Trim().Trim('"');

            string? folder = null, file = null;
            if (path.Length > 0)
            {
                if (await Task.Run(() => File.Exists(path)))
                {
                    file = Path.GetFullPath(path);
                    folder = Path.GetDirectoryName(file);
                }
                else if (await Task.Run(() => Directory.Exists(path)))
                    folder = Path.GetFullPath(path);
            }

            BringToFront();
            if (folder == null) return;

            if (file == null)
            {
                if (_viewerActive) LeaveViewer();
                await OpenFolderAsync(folder, null);
                return;
            }
            if (!ImageFiles.IsImage(Path.GetExtension(file))) return;

            int Find() => _items.ToList().FindIndex(x => string.Equals(x.FullPath, file, StringComparison.OrdinalIgnoreCase));

            bool sameFolder = _folder != null && string.Equals(
                Path.GetFullPath(_folder).TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            int idx = sameFolder ? Find() : -1;
            if (idx < 0)
            {
                await OpenFolderAsync(folder, file);   // anderer Ordner (oder neue Datei): Liste neu einlesen
                idx = Find();
            }
            if (idx < 0) return;

            if (_viewerActive)
            {
                _enterFit = true;   // Fenster wie beim Öffnen auf das neue Bild anpassen
                ShowIndex(idx);
            }
            else
            {
                FileList.SelectedIndex = idx;
                EnterViewer();
            }
        }
        catch { /* weitergeleiteter Pfad nicht lesbar - ignorieren */ }
    }

    void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            SystemCommands.RestoreWindow(this);
        Activate();
        if (!_fullscreen)
        {
            Topmost = true;    // Trick, damit das Fenster sicher nach vorne kommt
            Topmost = false;
        }
        Focus();
    }

    static string DefaultFolder()
    {
        foreach (var sf in new[] { Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyDocuments,
                                   Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.UserProfile })
        {
            var p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        return @"C:\";
    }

    // ============================================================ Vollbild

    void ToggleFullscreen()
    {
        if (!_viewerActive) return;
        if (_fullscreen) ExitFullscreen(restoreBounds: true);
        else EnterFullscreen();
    }

    void EnterFullscreen()
    {
        _fsWasMax = WindowState == WindowState.Maximized;
        _fsBounds = _fsWasMax ? RestoreBounds : new Rect(Left, Top, Width, Height);

        var screen = Native.GetMonitorBounds(this);
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Left = screen.Left;
        Top = screen.Top;
        Width = screen.Width;
        Height = screen.Height;
        _fullscreen = true;
    }

    void ExitFullscreen(bool restoreBounds)
    {
        _fullscreen = false;
        Topmost = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        if (!restoreBounds) return;

        Left = _fsBounds.Left;
        Top = _fsBounds.Top;
        Width = _fsBounds.Width;
        Height = _fsBounds.Height;
        if (_fsWasMax) WindowState = WindowState.Maximized;
    }

    // ============================================================ Ordnerbaum

    static IEnumerable<(string Name, string Path)> QuickFolders()
    {
        string G(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var list = new List<(string, string?)>
        {
            ("Bilder", G(Environment.SpecialFolder.MyPictures)),
            ("Desktop", G(Environment.SpecialFolder.DesktopDirectory)),
            ("Dokumente", G(Environment.SpecialFolder.MyDocuments)),
            ("Downloads", Native.GetDownloadsFolder()),
            ("Videos", G(Environment.SpecialFolder.MyVideos)),
            ("Musik", G(Environment.SpecialFolder.MyMusic)),
            ("Userhome", G(Environment.SpecialFolder.UserProfile)),
        };
        foreach (var (name, path) in list)
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                yield return (name, path);
    }

    void InitTree()
    {
        FolderTree.Items.Clear();
        _quickNodes.Clear();
        FolderTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(async (_, e) =>
        {
            if (e.OriginalSource is TreeViewItem n) await EnsureLoadedAsync(n);
        }));

        foreach (var (name, path) in QuickFolders())
        {
            var n = MakeNode(path, name);
            _quickNodes.Add(n);
            FolderTree.Items.Add(n);
        }

        _pcNode = new TreeViewItem { Header = "Dieser PC", Tag = "::pc", IsExpanded = true };
        foreach (var d in DriveInfo.GetDrives())
            _pcNode.Items.Add(MakeNode(d.Name, d.Name));
        FolderTree.Items.Add(_pcNode);
    }

    static TreeViewItem MakeNode(string path, string header)
    {
        var n = new TreeViewItem { Header = header, Tag = path };
        n.Items.Add(new TreeViewItem { Header = "…" });   // Platzhalter (Tag == null) -> wird beim Aufklappen ersetzt
        return n;
    }

    static List<string> ListSubDirs(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(d => d.Name)
                .OrderBy(n => n, Comparer<string>.Create(Native.StrCmpLogicalW))
                .ToList();
        }
        catch { return new List<string>(); }
    }

    async Task EnsureLoadedAsync(TreeViewItem n)
    {
        if (n.Items.Count == 1 && n.Items[0] is TreeViewItem d && d.Tag == null && n.Tag is string path)
        {
            var dirs = await Task.Run(() => ListSubDirs(path));
            n.Items.Clear();
            foreach (var name in dirs)
                n.Items.Add(MakeNode(Path.Combine(path, name), name));
        }
    }

    async Task SelectTreeAsync(string folder)
    {
        if (_pcNode == null) return;
        _suppressTree = true;
        try
        {
            string full = Path.GetFullPath(folder);

            // Ist es genau ein Systemordner (Bilder, Desktop ...), diesen Eintrag markieren
            string norm = full.TrimEnd('\\');
            var quick = _quickNodes.FirstOrDefault(n =>
                string.Equals(((string)n.Tag!).TrimEnd('\\'), norm, StringComparison.OrdinalIgnoreCase));
            if (quick != null)
            {
                quick.IsSelected = true;
                quick.BringIntoView();
                return;
            }

            // sonst den Weg unter "Dieser PC" aufklappen
            string root = Path.GetPathRoot(full) ?? "";
            var cur = _pcNode.Items.OfType<TreeViewItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, root, StringComparison.OrdinalIgnoreCase));
            if (cur == null) return;

            foreach (var seg in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                await EnsureLoadedAsync(cur);
                cur.IsExpanded = true;
                var next = cur.Items.OfType<TreeViewItem>()
                    .FirstOrDefault(i => string.Equals(i.Header as string, seg, StringComparison.OrdinalIgnoreCase));
                if (next == null) break;
                cur = next;
            }
            cur.IsSelected = true;
            cur.BringIntoView();
        }
        catch { /* Baum ist nur Navigationshilfe */ }
        finally { _suppressTree = false; }
    }

    async void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTree) return;
        if (e.NewValue is TreeViewItem t && t.Tag is string p && !p.StartsWith("::"))
            await OpenFolderAsync(p, null, fromTree: true);
    }

    // ============================================================ Ordner öffnen / Liste

    async Task OpenFolderAsync(string folder, string? selectFile, bool fromTree = false)
    {
        int token = ++_openToken;
        _folder = folder;
        FolderBox.Text = folder;
        _s.LastFolder = folder;

        List<ImageItem> list;
        try
        {
            var field = _s.SortField; var desc = _s.SortDescending;
            list = await Task.Run(() =>
            {
                var l = ImageFiles.Enumerate(folder);
                ImageFiles.Sort(l, field, desc);
                return l;
            });
        }
        catch (Exception ex)
        {
            if (token != _openToken) return;
            list = new List<ImageItem>();
            Title = $"{folder} – {ex.Message}";
        }
        if (token != _openToken) return;   // zwischenzeitlich anderer Ordner gewählt

        _items = new ObservableCollection<ImageItem>(list);
        FileList.ItemsSource = _items;
        _index = -1;

        if (_items.Count > 0)
        {
            int i = selectFile == null ? 0
                : Math.Max(0, _items.ToList().FindIndex(x => string.Equals(x.FullPath, selectFile, StringComparison.OrdinalIgnoreCase)));
            FileList.SelectedIndex = i;
            FileList.ScrollIntoView(_items[i]);
        }
        else ClearPreview();

        UpdateTitle();
        if (!fromTree) await SelectTreeAsync(folder);
    }

    Task ReloadAsync()
    {
        var cur = CurrentItem()?.FullPath;
        return _folder == null ? Task.CompletedTask : OpenFolderAsync(_folder, cur);
    }

    ImageItem? CurrentItem() =>
        _viewerActive ? (_index >= 0 && _index < _items.Count ? _items[_index] : null)
                      : FileList.SelectedItem as ImageItem;

    void UpdateTitle()
    {
        if (_viewerActive && _index >= 0 && _index < _items.Count)
            Title = $"{_items[_index].Name}  [{_index + 1}/{_items.Count}]  {Viewer.Zoom * 100:0}%  – Bildbetrachter";
        else
            Title = _folder == null ? "Bildbetrachter" : $"{_folder}  ({_items.Count} Bilder) – Bildbetrachter";
    }

    // ============================================================ Vorschau + Bildinfos

    void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedIndex >= 0) _index = FileList.SelectedIndex;
        _ = UpdatePreviewAsync();
    }

    void ClearPreview()
    {
        _previewCts?.Cancel();
        PreviewImage.Source = null;
        InfoText.Text = "";
        MetaPanel.Visibility = Visibility.Collapsed;
        InfoButton.Visibility = Visibility.Collapsed;
    }

    async Task UpdatePreviewAsync()
    {
        _previewCts?.Cancel();
        if (FileList.SelectedItem is not ImageItem item) { ClearPreview(); return; }

        var cts = _previewCts = new CancellationTokenSource();
        try
        {
            var img = await _svc.GetAsync(item.FullPath, 1600, cts.Token);
            if (cts.IsCancellationRequested) return;

            PreviewImage.Source = img.Bitmap;
            InfoText.Text = BuildInfoText(item, img);

            string? meta = null;
            if (item.FullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                meta = await Task.Run(() => PngMetadata.Read(item.FullPath));
            if (cts.IsCancellationRequested) return;

            MetaBox.Text = meta ?? "";
            MetaPanel.Visibility = meta != null ? Visibility.Visible : Visibility.Collapsed;
            InfoButton.Visibility = meta != null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            PreviewImage.Source = null;
            InfoText.Text = "Fehler beim Laden:\n" + ex.Message;
            MetaPanel.Visibility = Visibility.Collapsed;
            InfoButton.Visibility = Visibility.Collapsed;
        }
    }

    static string BuildInfoText(ImageItem item, LoadedImage img)
    {
        var c = CultureInfo.CurrentCulture;
        long px = (long)img.OriginalWidth * img.OriginalHeight;
        return string.Join("\n",
            $"Format:      {img.Format}",
            $"Größe:       {img.OriginalWidth} × {img.OriginalHeight} px",
            $"Pixelzahl:   {px / 1_000_000.0:0.0} MP ({px.ToString("N0", c)})",
            $"Farbstufen:  {ColorText(img.BitsPerPixel)}",
            $"Dateigröße:  {item.Size.ToString("N0", c)} Byte");
    }

    static string ColorText(int bpp) => bpp switch
    {
        1 => "1 Bit (2 Farben)",
        >= 2 and <= 8 => $"{bpp} Bit ({1 << bpp} Farben)",
        24 => "24 Bit (16,7 Mio. Farben)",
        32 => "32 Bit (16,7 Mio. Farben + Alpha)",
        _ => $"{bpp} Bit"
    };

    void ApplyInfoVisibility() =>
        InfoPanel.Visibility = _s.ShowInfo ? Visibility.Visible : Visibility.Collapsed;

    void ApplyThumbnailMode()
    {
        var gv = (GridView)FileList.View;
        bool has = gv.Columns.Contains(_colThumb);
        if (_s.ShowThumbnails && !has) gv.Columns.Insert(0, _colThumb);
        else if (!_s.ShowThumbnails && has) gv.Columns.Remove(_colThumb);
    }

    void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        _s.ShowInfo = !_s.ShowInfo;
        ApplyInfoVisibility();
        _s.Save();
    }

    void CopyMeta_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(MetaBox.Text); } catch { /* Zwischenablage gerade gesperrt */ }
    }

    // ============================================================ Liste: Eingaben

    void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileList, d) is ListViewItem)
            EnterViewer();
    }

    void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EnterViewer(); e.Handled = true; }
    }

    void FileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileList, d) is ListViewItem lvi)
            lvi.IsSelected = true;
    }

    // ============================================================ Ansicht "Bild"

    void EnterViewer()
    {
        if (_items.Count == 0) return;
        if (FileList.SelectedIndex >= 0) _index = FileList.SelectedIndex;
        if (_index < 0) _index = 0;

        CaptureListState();   // Listenfenster-Größe merken, bevor das Fenster auf das Bild angepasst wird

        _viewerActive = true;
        MinWidth = ViewerMinW;
        MinHeight = ViewerMinH;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

        ListRoot.Visibility = Visibility.Collapsed;
        ViewerRoot.Visibility = Visibility.Visible;
        ViewerRoot.UpdateLayout();
        Viewer.AutoFit = _mode == ZoomMode.Optimal;
        _enterFit = true;
        Keyboard.Focus(Viewer);
        ShowIndex(_index);
    }

    void LeaveViewer()
    {
        _viewCts?.Cancel();
        _prefetchCts?.Cancel();
        _viewerActive = false;
        _enterFit = false;
        Viewer.Clear();
        ViewerRoot.Visibility = Visibility.Collapsed;
        ListRoot.Visibility = Visibility.Visible;
        RestoreListWindow();

        if (_index >= 0 && _index < _items.Count)
        {
            FileList.SelectedIndex = _index;
            FileList.ScrollIntoView(_items[_index]);
        }
        FileList.Focus();
        (FileList.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, _index)) as ListViewItem)?.Focus();
        UpdateTitle();
    }

    void Step(int delta)
    {
        if (!_viewerActive || _items.Count == 0) return;
        _lastDir = delta >= 0 ? 1 : -1;
        int target = Math.Clamp(_index + delta, 0, _items.Count - 1);
        if (target != _index) ShowIndex(target);
    }

    async void ShowIndex(int i)
    {
        if (_items.Count == 0) return;
        i = Math.Clamp(i, 0, _items.Count - 1);
        _index = i;

        _viewCts?.Cancel();
        var cts = _viewCts = new CancellationTokenSource();
        var item = _items[i];
        UpdateTitle();

        try
        {
            LoadedImage? li;
            if (!_svc.TryGetCached(item.FullPath, 0, out li) || li == null)
                li = await _svc.GetAsync(item.FullPath, 0, cts.Token);
            if (cts.IsCancellationRequested || !_viewerActive) return;

            Viewer.SetImage(li, _mode);   // löst ZoomChanged aus -> Fenster wächst bei Bedarf
            if (_enterFit)
            {
                _enterFit = false;
                FitWindowToImage(center: true, allowShrink: true);
            }
            UpdateTitle();
            Prefetch(i);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Viewer.Clear();
            Title = $"{item.Name} – Fehler: {ex.Message}";
        }
    }

    /// <summary>Lädt die nächsten Bilder in Blätterrichtung im Hintergrund vor (verzögert, damit schnelles Scrollen nichts blockiert).</summary>
    void Prefetch(int center)
    {
        _prefetchCts?.Cancel();
        var cts = _prefetchCts = new CancellationTokenSource();
        var paths = new List<string>();
        foreach (int off in new[] { 1, 2, -1 })
        {
            int j = center + off * _lastDir;
            if (j >= 0 && j < _items.Count) paths.Add(_items[j].FullPath);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, cts.Token);
                foreach (var p in paths)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    await _svc.GetAsync(p, 0, cts.Token);
                }
            }
            catch { /* abgebrochen oder Bild defekt - egal */ }
        });
    }

    /// <summary>
    /// Passt das Fenster an die Bildgröße an (höchstens bis zum Arbeitsbereich des Monitors).
    /// center = true beim Wechsel in die Bildansicht: Fenster mittig auf dem Monitor.
    /// center = false: Fenster ändert sich um seine Mitte herum (wandert nicht davon).
    /// allowShrink = false: Fenster wächst nur; true: Fenster schrumpft auch auf die Bildgröße.
    /// follow = true: das Bild bleibt dabei auf dem Bildschirm stehen (Zoom durch den Benutzer).
    /// Im Modus "Optimal" gilt die Originalgröße des Bildes als Wunschgröße (danach wird eingepasst).
    /// </summary>
    void FitWindowToImage(bool center, bool allowShrink, bool follow = false)
    {
        if (!_viewerActive || _fullscreen || WindowState != WindowState.Normal || !Viewer.HasImage) return;
        if (Viewer.ActualWidth < 1 || Viewer.ActualHeight < 1) return;

        var want = Viewer.AutoFit && !Viewer.UserZoomed ? Viewer.NaturalSize : Viewer.DisplaySize;
        var wa = Native.GetWorkArea(this);

        // Fensterrahmen + Titelleiste = Fenster minus Anzeigebereich (beide Werte aus demselben Layout-Stand)
        double chromeW = ActualWidth - Viewer.ActualWidth;
        double chromeH = ActualHeight - Viewer.ActualHeight;

        double w = Math.Min(want.Width + chromeW, wa.Width);
        double h = Math.Min(want.Height + chromeH, wa.Height);
        if (!allowShrink)
        {
            w = Math.Max(w, ActualWidth);
            h = Math.Max(h, ActualHeight);
        }
        w = Math.Max(w, MinWidth);
        h = Math.Max(h, MinHeight);
        if (Math.Abs(w - Width) < 0.5 && Math.Abs(h - Height) < 0.5 && !center) return;

        double oldLeft = Left, oldTop = Top;
        double newLeft, newTop;
        if (center)
        {
            newLeft = wa.Left + (wa.Width - w) / 2;
            newTop = wa.Top + (wa.Height - h) / 2;
        }
        else
        {
            // um die Fenstermitte herum wachsen/schrumpfen, im Arbeitsbereich bleiben
            newLeft = Math.Max(wa.Left, Math.Min(oldLeft + (Width - w) / 2, wa.Right - w));
            newTop = Math.Max(wa.Top, Math.Min(oldTop + (Height - h) / 2, wa.Bottom - h));
        }

        Width = w;
        Height = h;
        Left = newLeft;
        Top = newTop;

        // Bild um die Fensterverschiebung nachführen, damit es auf dem Bildschirm stehen bleibt
        if (follow && !center)
            Viewer.FollowWindow(w - chromeW, h - chromeH, oldLeft - newLeft, oldTop - newTop);
    }

    // ============================================================ Tastatur

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewerActive)
        {
            if (e.Key == Key.F5) { _ = ReloadAsync(); e.Handled = true; }
            return;
        }

        const double Pan = 80;
        switch (e.Key)
        {
            case Key.PageDown: Step(1); break;
            case Key.PageUp: Step(-1); break;
            case Key.Home: _lastDir = -1; ShowIndex(0); break;
            case Key.End: _lastDir = 1; ShowIndex(_items.Count - 1); break;
            case Key.Escape: LeaveViewer(); break;
            case Key.F11: ToggleFullscreen(); break;
            case Key.Divide: Viewer.ApplyMode(ZoomMode.Original); break;   // Ziffernblock "/" = 100 %
            case Key.Multiply: ZoomOptimal(); break;                        // Ziffernblock "*" = optimale Größe
            case Key.Add: case Key.OemPlus: Viewer.ZoomStep(1); break;
            case Key.Subtract: case Key.OemMinus: Viewer.ZoomStep(-1); break;
            case Key.Left: Viewer.PanView(-Pan, 0); break;
            case Key.Right: Viewer.PanView(Pan, 0); break;
            case Key.Up: Viewer.PanView(0, -Pan); break;
            case Key.Down: Viewer.PanView(0, Pan); break;
            default: return;
        }
        e.Handled = true;
    }

    // ============================================================ Kontextmenü

    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Verschieben …", (_, _) => MoveOrCopy(move: true)));
        menu.Items.Add(Item("Kopieren …", (_, _) => MoveOrCopy(move: false)));
        menu.Items.Add(new Separator());

        var sortMenu = new MenuItem { Header = "Sortieren" };
        var sortItems = new List<(MenuItem Mi, SortField F, bool Desc)>();
        void AddSort(string text, SortField f, bool desc)
        {
            var mi = Item(text, (_, _) => { _s.SortField = f; _s.SortDescending = desc; ApplySort(); _s.Save(); });
            sortMenu.Items.Add(mi);
            sortItems.Add((mi, f, desc));
        }
        AddSort("Geändert am – aufsteigend", SortField.Modified, false);
        AddSort("Geändert am – absteigend", SortField.Modified, true);
        sortMenu.Items.Add(new Separator());
        AddSort("Erstellt am – aufsteigend", SortField.Created, false);
        AddSort("Erstellt am – absteigend", SortField.Created, true);
        sortMenu.Items.Add(new Separator());
        AddSort("Name – aufsteigend (A–Z)", SortField.Name, false);
        AddSort("Name – absteigend (Z–A)", SortField.Name, true);
        menu.Items.Add(sortMenu);

        var zoomMenu = new MenuItem { Header = "Bilder beim Laden anzeigen" };
        var zoomItems = new List<(MenuItem Mi, ZoomMode M)>();
        void AddZoom(string text, ZoomMode m)
        {
            var mi = Item(text, (_, _) => SetMode(m));
            zoomMenu.Items.Add(mi);
            zoomItems.Add((mi, m));
        }
        AddZoom("In optimaler Größe (einpassen)", ZoomMode.Optimal);
        AddZoom("In aktueller Zoomstufe", ZoomMode.Current);
        AddZoom("In Originalgröße (100 %)", ZoomMode.Original);
        menu.Items.Add(zoomMenu);

        menu.Items.Add(new Separator());
        var thumbs = Item("Miniaturvorschau (50 × 50)", (_, _) =>
        {
            _s.ShowThumbnails = !_s.ShowThumbnails;
            ApplyThumbnailMode();
            _s.Save();
        });
        menu.Items.Add(thumbs);

        var info = Item("Bildinformationen anzeigen", (_, _) =>
        {
            _s.ShowInfo = !_s.ShowInfo;
            ApplyInfoVisibility();
            _s.Save();
        });
        menu.Items.Add(info);

        menu.Opened += (_, _) =>
        {
            foreach (var (mi, f, desc) in sortItems) mi.IsChecked = _s.SortField == f && _s.SortDescending == desc;
            foreach (var (mi, m) in zoomItems) mi.IsChecked = _mode == m;
            thumbs.IsChecked = _s.ShowThumbnails;
            info.IsChecked = _s.ShowInfo;
        };
        return menu;
    }

    static MenuItem Item(string header, RoutedEventHandler click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += click;
        return mi;
    }

    void SetMode(ZoomMode m)
    {
        _mode = m;
        Viewer.AutoFit = m == ZoomMode.Optimal;
        if (_viewerActive && Viewer.HasImage)
        {
            if (m == ZoomMode.Optimal) ZoomOptimal();
            else Viewer.ApplyMode(m);
            UpdateTitle();
        }
    }

    /// <summary>
    /// Optimale Größe: Bild so einpassen, dass es (höchstens mit 100 %) in den Arbeitsbereich des Monitors passt;
    /// das Fenster passt sich entsprechend an. Danach folgt die Einpassung auch Größenänderungen des Fensters.
    /// </summary>
    void ZoomOptimal()
    {
        if (!_viewerActive || !Viewer.HasImage) return;
        Viewer.AutoFit = true;

        if (_fullscreen || WindowState != WindowState.Normal || Viewer.ActualWidth < 1 || Viewer.ActualHeight < 1)
        {
            Viewer.ApplyOptimal();   // Vollbild/maximiert: in den vorhandenen Ausschnitt einpassen
            return;
        }

        var wa = Native.GetWorkArea(this);
        double chromeW = ActualWidth - Viewer.ActualWidth;
        double chromeH = ActualHeight - Viewer.ActualHeight;
        Viewer.ApplyOptimal(wa.Width - chromeW, wa.Height - chromeH);
    }

    void ApplySort()
    {
        var current = CurrentItem();
        var list = _items.ToList();
        ImageFiles.Sort(list, _s.SortField, _s.SortDescending);
        _items = new ObservableCollection<ImageItem>(list);
        FileList.ItemsSource = _items;

        _index = current == null ? -1 : list.FindIndex(x => x.FullPath == current.FullPath);
        if (_index >= 0)
        {
            if (!_viewerActive) { FileList.SelectedIndex = _index; FileList.ScrollIntoView(_items[_index]); }
            UpdateTitle();
        }
    }

    // ============================================================ Verschieben / Kopieren

    void MoveOrCopy(bool move)
    {
        var item = CurrentItem();
        if (item == null) return;

        var dlg = new FolderListDialog(_s, move ? "Verschieben nach …" : "Kopieren nach …") { Owner = this };
        bool ok = dlg.ShowDialog() == true;
        _s.Save();   // Änderungen an der Ordnerliste immer behalten
        if (!ok || dlg.Selected == null) return;

        string destDir = dlg.Selected;
        bool sameFolder = _folder != null && string.Equals(
            Path.GetFullPath(destDir).TrimEnd('\\'), Path.GetFullPath(_folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (sameFolder && move) return;

        try
        {
            string dest = UniqueTarget(destDir, item.Name);
            if (move) File.Move(item.FullPath, dest);
            else File.Copy(item.FullPath, dest);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, move ? "Verschieben fehlgeschlagen" : "Kopieren fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (move) RemoveItem(item);
        else if (sameFolder) _ = ReloadAsync();
    }

    static string UniqueTarget(string dir, string name)
    {
        string target = Path.Combine(dir, name);
        if (!File.Exists(target)) return target;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            target = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(target)) return target;
        }
    }

    void RemoveItem(ImageItem item)
    {
        int idx = _items.IndexOf(item);
        if (idx < 0) return;
        _items.Remove(item);

        if (_items.Count == 0)
        {
            _index = -1;
            if (_viewerActive) LeaveViewer();
            ClearPreview();
            UpdateTitle();
            return;
        }

        _index = Math.Min(idx, _items.Count - 1);
        if (_viewerActive) ShowIndex(_index);
        else { FileList.SelectedIndex = _index; UpdateTitle(); }
    }
}
