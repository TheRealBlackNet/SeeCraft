using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfAnimatedGif;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Bildbetrachter;

/// <summary>
/// Bildansicht mit Zoom und Verschieben.
///  - Ziehen mit der linken Maustaste: ist das Bild groesser als der Ausschnitt -> verschieben,
///    sonst -> Rechteck aufziehen und auf den Bereich zoomen (mit Shift immer Zoom-Rechteck).
///  - Mausrad: naechstes/vorheriges Bild (Event NavigateRequested), mit Strg: Zoom.
/// </summary>
public sealed class ZoomView : Grid
{
    public static readonly double[] Steps =
        { 0.10, 0.20, 0.30, 0.50, 0.70, 1.00, 1.25, 1.50, 2.00, 3.00, 5.00, 7.00, 10.00, 20.00, 30.00, 50.00 };

    const double MinZoom = 0.02, MaxZoom = 50.0;

    readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Black };
    readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, SnapsToDevicePixels = false };
    readonly Rectangle _band = new()
    {
        Stroke = Brushes.White,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };

    BitmapSource? _bmp;
    double _zoom = 1, _x, _y;
    bool _userZoomed;

    enum DragMode { None, Pan, Band }
    DragMode _drag;
    Point _dragStart;
    double _dragX0, _dragY0;

    /// <summary>Bei Modus "Optimal" passt sich das Bild beim Aendern der Fenstergroesse an.</summary>
    public bool AutoFit { get; set; }
    public double Zoom => _zoom;
    public bool HasImage => _bmp != null;

    public event EventHandler? ZoomChanged;
    public event Action<int>? NavigateRequested;

    public ZoomView()
    {
        Focusable = true;
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_band);
        Children.Add(_canvas);

        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;
        MouseWheel += OnWheel;
        SizeChanged += (_, _) =>
        {
            if (_bmp == null) return;
            if (AutoFit && !_userZoomed) _zoom = FitZoom();
            Relayout();
        };
    }

    // ---- Geometrie -------------------------------------------------------------------------

    /// <summary>WPF-Einheiten pro Bildschirmpixel (bei 150 % Skalierung 0,667).</summary>
    double F => 1.0 / VisualTreeHelper.GetDpi(this).DpiScaleX;

    public Size DisplaySize => _bmp == null ? new Size(0, 0)
        : new Size(_bmp.PixelWidth * _zoom * F, _bmp.PixelHeight * _zoom * F);

    double FitZoom()
    {
        if (_bmp == null || ActualWidth < 1 || ActualHeight < 1) return 1;
        double z = Math.Min(ActualWidth / (_bmp.PixelWidth * F), ActualHeight / (_bmp.PixelHeight * F));
        return Math.Min(z, 1.0); // nur verkleinern, nie ueber 100 % vergroessern
    }

    void Relayout()
    {
        if (_bmp == null) { _image.Visibility = Visibility.Hidden; return; }

        var d = DisplaySize;
        double vw = ActualWidth, vh = ActualHeight;
        _x = d.Width <= vw ? (vw - d.Width) / 2 : Math.Clamp(_x, vw - d.Width, 0);
        _y = d.Height <= vh ? (vh - d.Height) / 2 : Math.Clamp(_y, vh - d.Height, 0);

        _image.Width = d.Width;
        _image.Height = d.Height;
        Canvas.SetLeft(_image, _x);
        Canvas.SetTop(_image, _y);
        _image.Visibility = Visibility.Visible;

        RenderOptions.SetBitmapScalingMode(_image,
            _zoom >= 2 ? BitmapScalingMode.NearestNeighbor
            : _zoom < 1 ? BitmapScalingMode.HighQuality
            : BitmapScalingMode.Linear);
    }

    void Notify() => ZoomChanged?.Invoke(this, EventArgs.Empty);

    // ---- oeffentliche Steuerung -------------------------------------------------------------

    public void SetImage(LoadedImage li, ZoomMode mode)
    {
        _bmp = li.Bitmap;
        if (li.IsAnimatedGif)
        {
            ImageBehavior.SetAnimatedSource(_image, _bmp);
        }
        else
        {
            ImageBehavior.SetAnimatedSource(_image, null);
            _image.Source = _bmp;
        }

        switch (mode)
        {
            case ZoomMode.Original: _zoom = 1; _userZoomed = true; break;
            case ZoomMode.Optimal: _zoom = FitZoom(); _userZoomed = false; break;
            default: _zoom = Math.Clamp(_zoom, MinZoom, MaxZoom); _userZoomed = true; break;
        }

        var d = DisplaySize;
        _x = (ActualWidth - d.Width) / 2;   // neues Bild immer zentriert beginnen
        _y = (ActualHeight - d.Height) / 2;
        Relayout();
        Notify();
    }

    public void Clear()
    {
        _bmp = null;
        ImageBehavior.SetAnimatedSource(_image, null);
        _image.Source = null;
        Relayout();
    }

    /// <summary>Modus aendern, waehrend ein Bild angezeigt wird.</summary>
    public void ApplyMode(ZoomMode mode)
    {
        if (_bmp == null) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (mode == ZoomMode.Original) ZoomTo(1, center);
        else if (mode == ZoomMode.Optimal)
        {
            _userZoomed = false;
            _zoom = FitZoom();
            var d = DisplaySize;
            _x = (ActualWidth - d.Width) / 2;
            _y = (ActualHeight - d.Height) / 2;
            Relayout();
            Notify();
        }
    }

    public void ZoomTo(double zoom, Point anchor)
    {
        if (_bmp == null) return;
        var d0 = DisplaySize;
        double fx = d0.Width > 0 ? (anchor.X - _x) / d0.Width : 0.5;
        double fy = d0.Height > 0 ? (anchor.Y - _y) / d0.Height : 0.5;

        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _userZoomed = true;

        var d1 = DisplaySize;
        _x = anchor.X - fx * d1.Width;
        _y = anchor.Y - fy * d1.Height;
        Relayout();
        Notify();
    }

    /// <summary>Naechste feste Zoomstufe (10 % ... 5000 %); liegt der Zoom dazwischen, geht es zur naechsten Stufe.</summary>
    public void ZoomStep(int direction, Point? anchor = null)
    {
        double z = _zoom;
        double next = direction > 0
            ? Steps.FirstOrDefault(s => s > z * 1.001, Steps[^1])
            : Steps.LastOrDefault(s => s < z * 0.999, Steps[0]);
        ZoomTo(next, anchor ?? new Point(ActualWidth / 2, ActualHeight / 2));
    }

    /// <summary>Bildausschnitt verschieben (Pfeiltasten). Positive Werte = Ausschnitt nach rechts/unten.</summary>
    public void PanView(double dx, double dy)
    {
        if (_bmp == null) return;
        _x -= dx;
        _y -= dy;
        Relayout();
    }

    // ---- Maus -------------------------------------------------------------------------------

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_bmp == null) return;
        Keyboard.Focus(this);
        _canvas.CaptureMouse();
        _dragStart = e.GetPosition(_canvas);

        var d = DisplaySize;
        bool scrollable = d.Width > ActualWidth + 0.5 || d.Height > ActualHeight + 0.5;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (scrollable && !shift)
        {
            _drag = DragMode.Pan;
            _dragX0 = _x;
            _dragY0 = _y;
            Cursor = Cursors.SizeAll;
        }
        else
        {
            _drag = DragMode.Band;
            _band.Visibility = Visibility.Visible;
            UpdateBand(_dragStart);
            Cursor = Cursors.Cross;
        }
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var p = e.GetPosition(_canvas);
        if (_drag == DragMode.Pan)
        {
            _x = _dragX0 + (p.X - _dragStart.X);
            _y = _dragY0 + (p.Y - _dragStart.Y);
            Relayout();
        }
        else UpdateBand(p);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var p = e.GetPosition(_canvas);
        var mode = _drag;
        _drag = DragMode.None;
        _canvas.ReleaseMouseCapture();
        Cursor = null;
        _band.Visibility = Visibility.Collapsed;

        if (mode == DragMode.Band)
        {
            var r = new Rect(_dragStart, p);
            if (r.Width > 8 && r.Height > 8) ZoomToRect(r);
        }
    }

    void UpdateBand(Point p)
    {
        Canvas.SetLeft(_band, Math.Min(_dragStart.X, p.X));
        Canvas.SetTop(_band, Math.Min(_dragStart.Y, p.Y));
        _band.Width = Math.Abs(p.X - _dragStart.X);
        _band.Height = Math.Abs(p.Y - _dragStart.Y);
    }

    void ZoomToRect(Rect r)
    {
        if (_bmp == null) return;
        double s = _zoom * F;                         // WPF-Einheiten pro Bildpixel
        double px0 = (r.X - _x) / s, py0 = (r.Y - _y) / s;
        double pxW = r.Width / s, pxH = r.Height / s;

        double newS = Math.Min(ActualWidth / pxW, ActualHeight / pxH);
        _zoom = Math.Clamp(newS / F, MinZoom, MaxZoom);
        _userZoomed = true;

        newS = _zoom * F;
        _x = ActualWidth / 2 - (px0 + pxW / 2) * newS;
        _y = ActualHeight / 2 - (py0 + pxH / 2) * newS;
        Relayout();
        Notify();
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomStep(e.Delta > 0 ? 1 : -1, e.GetPosition(this));
        else
            NavigateRequested?.Invoke(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }
}
