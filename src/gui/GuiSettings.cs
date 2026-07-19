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
    public bool InstallPromptDismissed { get; set; } // the first-run "install to %LOCALAPPDATA%" prompt was answered/dismissed
    public bool TechColumns { get; set; }            // show the diagnostic mod-table columns (Source/Class/Data/Workshop ID)

    private static string PathFor() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ostrasort", "settings.json");

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
