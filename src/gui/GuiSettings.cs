using System.IO;
using System.Text.Json;

namespace Ostrasort.Gui;

/// <summary>Small persisted UI state: window placement, tidy toggle, selected tab.</summary>
public sealed class GuiSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1120;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; }
    public bool Tidy { get; set; }
    public int Tab { get; set; }
    public string Theme { get; set; } = "system";   // "system" | "light" | "dark"
    public bool TechColumns { get; set; }            // show the diagnostic mod-table columns (Source/Class/Data/Workshop ID)
    public bool HoldNew { get; set; }                // register mods missing from the load order turned off (the game's |disabled marker)
    public bool BackupSaves { get; set; } = true;    // zip the Saves folder before risky changes (see SaveBackups)

    private static string PathFor() => AppPaths.File("settings.json");

    public static GuiSettings Load()
    {
        try
        {
            var p = PathFor();
            if (File.Exists(p))
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(p)) ?? new GuiSettings();
        }
        catch { /* corrupt settings are not worth an error - defaults win */ }
        return new GuiSettings();
    }

    public void Save()
    {
        try
        {
            var p = PathFor();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
