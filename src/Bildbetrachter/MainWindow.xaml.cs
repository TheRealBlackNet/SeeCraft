using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;

namespace Bildbetrachter;

public partial class MainWindow : Window
{
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

    CancellationTokenSource? _viewCts, _previewCts, _prefetchCts;
    TreeViewItem? _pcNode;

    public MainWindow(string? startArg)
    {
        InitializeComponent();
        RestoreWindow();

        ListRoot.ColumnDefinitions[0].Width = new GridLength(Math.Max(120, _s.TreeWidth));
        ListRoot.ColumnDefinitions[2].Width = new GridLength(Math.Max(120, _s.PreviewWidth));

        FileList.ContextMenu = BuildMenu();
        Viewer.ContextMenu = BuildMenu();
        Viewer.ZoomChanged += (_, _) => { GrowToFit(); UpdateTitle(); };
        Viewer.NavigateRequested += d => Step(d);
        ApplyInfoVisibility();

        Loaded += async (_, _) => await StartAsync(startArg);
    }

    // ============================================================ Start / Fenster

    void RestoreWindow()
    {
        if (_s.Width is double w && _s.Height is double h && w > 300 && h > 200)
        {
            Width = w; Height = h;
            if (_s.Left is double l && _s.Top is double t
                && l > SystemParameters.VirtualScreenLeft - 20 && t > SystemParameters.VirtualScreenTop - 20
                && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
                && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l; Top = t;
            }
        }
        if (_s.Maximized) WindowState = WindowState.Maximized;
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var rb = RestoreBounds;
        _s.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) rb = new Rect(Left, Top, Width, Height);
        _s.Left = rb.Left; _s.Top = rb.Top; _s.Width = rb.Width; _s.Height = rb.Height;
        _s.TreeWidth = ListRoot.ColumnDefinitions[0].ActualWidth;
        _s.PreviewWidth = ListRoot.ColumnDefinitions[2].ActualWidth;
        _s.Save();
    }

    async Task StartAsync(string? arg)
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

    // ============================================================ Ordnerbaum

    void InitTree()
    {
        FolderTree.Items.Clear();
        FolderTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(async (_, e) =>
        {
            if (e.OriginalSource is TreeViewItem n) await EnsureLoadedAsync(n);
        }));

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

    void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        _s.ShowInfo = !_s.ShowInfo;
        ApplyInfoVisibility();
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

        _viewerActive = true;
        ListRoot.Visibility = Visibility.Collapsed;
        ViewerRoot.Visibility = Visibility.Visible;
        ViewerRoot.UpdateLayout();
        Viewer.AutoFit = _mode == ZoomMode.Optimal;
        Keyboard.Focus(Viewer);
        ShowIndex(_index);
    }

    void LeaveViewer()
    {
        _viewCts?.Cancel();
        _prefetchCts?.Cancel();
        _viewerActive = false;
        Viewer.Clear();
        ViewerRoot.Visibility = Visibility.Collapsed;
        ListRoot.Visibility = Visibility.Visible;

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

            Viewer.SetImage(li, _mode);   // löst ZoomChanged aus -> Fenster passt sich an, Titel wird aktualisiert
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

    /// <summary>Fenster wächst mit dem Zoom mit (bis zum Arbeitsbereich des Monitors), schrumpft aber nie von selbst.</summary>
    void GrowToFit()
    {
        if (!_viewerActive || WindowState != WindowState.Normal || _mode == ZoomMode.Optimal || !Viewer.HasImage) return;
        if (Viewer.ActualWidth < 1) return;

        var disp = Viewer.DisplaySize;
        double extraW = disp.Width - Viewer.ActualWidth;
        double extraH = disp.Height - Viewer.ActualHeight;
        if (extraW <= 0.5 && extraH <= 0.5) return;

        var wa = Native.GetWorkArea(this);
        double newW = Math.Min(Width + Math.Max(0, extraW), wa.Width);
        double newH = Math.Min(Height + Math.Max(0, extraH), wa.Height);
        Width = newW;
        Height = newH;
        Left = Math.Max(wa.Left, Math.Min(Left, wa.Right - newW));
        Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - newH));
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
            var mi = Item(text, (_, _) => { _s.SortField = f; _s.SortDescending = desc; ApplySort(); });
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

        var info = Item("Bildinformationen anzeigen", (_, _) => { _s.ShowInfo = !_s.ShowInfo; ApplyInfoVisibility(); });
        menu.Items.Add(info);

        menu.Opened += (_, _) =>
        {
            foreach (var (mi, f, desc) in sortItems) mi.IsChecked = _s.SortField == f && _s.SortDescending == desc;
            foreach (var (mi, m) in zoomItems) mi.IsChecked = _mode == m;
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
            Viewer.ApplyMode(m);
            UpdateTitle();
        }
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
