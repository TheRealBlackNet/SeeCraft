using System.Text.Json;

namespace Bildbetrachter;

public enum SortField { Modified, Created, Name }

public enum ZoomMode { Original, Optimal, Current }

/// <summary>Einstellungen in %APPDATA%\Bildbetrachter\settings.json</summary>
public sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public SortField SortField { get; set; } = SortField.Name;
    public bool SortDescending { get; set; }
    public bool ShowInfo { get; set; }
    public List<string> TargetFolders { get; set; } = new();

    public bool ShowThumbnails { get; set; }

    // Spaltenbreiten der Listenansicht
    public double TreeWidth { get; set; } = 260;
    public double PreviewWidth { get; set; } = 380;
    public double ColName { get; set; } = 230;
    public double ColModified { get; set; } = 130;
    public double ColSize { get; set; } = 80;

    // Fenstergröße der LISTENansicht (die Bildansicht hat eine eigene, nicht gespeicherte Größe)
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bildbetrachter", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* defekte Datei -> Defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Einstellungen sind nicht kritisch */ }
    }
}
