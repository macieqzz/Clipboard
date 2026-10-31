using System.IO;
using System.Text.Json;

namespace Clipwise;

/// <summary>App-level switches persisted next to the store.</summary>
public class Settings
{
    private static readonly string File =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipwise", "settings.json");

    /// <summary>When true, Win+V opens Clipwise instead of the Windows clipboard history.</summary>
    public bool WinVOverride { get; set; } = true;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this));
        }
        catch { /* non-fatal */ }
    }

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new Settings();
        }
        catch { /* fall back to defaults */ }
        return new Settings();
    }
}
