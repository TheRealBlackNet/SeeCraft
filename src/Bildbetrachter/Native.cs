using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Bildbetrachter;

internal static class Native
{
    /// <summary>Windows-Explorer-Sortierung: Zahlen werden numerisch verglichen (ABC_2 &lt; ABC_10).</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int StrCmpLogicalW(string x, string y);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Downloads-Ordner (kein Eintrag in Environment.SpecialFolder vorhanden).</summary>
    public static string? GetDownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>Arbeitsbereich (ohne Taskleiste) des Monitors, auf dem das Fenster liegt, in WPF-Einheiten.</summary>
    public static Rect GetWorkArea(Window w) => GetMonitorRect(w, work: true);

    /// <summary>Gesamte Fläche des Monitors (für Vollbild), in WPF-Einheiten.</summary>
    public static Rect GetMonitorBounds(Window w) => GetMonitorRect(w, work: false);

    static Rect GetMonitorRect(Window w, bool work)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hwnd == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi))
            return work ? SystemParameters.WorkArea
                        : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

        var r = work ? mi.rcWork : mi.rcMonitor;
        var m = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice;
        double sx = m?.M11 ?? 1, sy = m?.M22 ?? 1;
        return new Rect(r.Left * sx, r.Top * sy, (r.Right - r.Left) * sx, (r.Bottom - r.Top) * sy);
    }
}
