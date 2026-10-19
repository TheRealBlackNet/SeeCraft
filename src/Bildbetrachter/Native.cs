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

    /// <summary>Arbeitsbereich (ohne Taskleiste) des Monitors, auf dem das Fenster liegt, in WPF-Einheiten.</summary>
    public static Rect GetWorkArea(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hwnd == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi))
            return SystemParameters.WorkArea;

        var m = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice;
        double sx = m?.M11 ?? 1, sy = m?.M22 ?? 1;
        return new Rect(mi.rcWork.Left * sx, mi.rcWork.Top * sy,
                        (mi.rcWork.Right - mi.rcWork.Left) * sx,
                        (mi.rcWork.Bottom - mi.rcWork.Top) * sy);
    }
}
