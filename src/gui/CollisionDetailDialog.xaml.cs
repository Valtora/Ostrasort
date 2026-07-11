using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostrasort.Gui;

/// <summary>
/// Read-only side-by-side view of one collision: the base game's object (when
/// there is one) and each claimant's version, laid out column-per-source with
/// changed fields highlighted and disagreements flagged. Presentation only -
/// the model and its change/contest logic live in <see cref="CollisionDetail"/>.
/// </summary>
public partial class CollisionDetailDialog : Window
{
    private static readonly FontFamily Mono = new("Consolas");
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly GameEnv _env;
    private readonly Collision _collision;
    private readonly IReadOnlyList<string>? _ignore;

    public CollisionDetailDialog(GameEnv env, Collision collision, IReadOnlyList<string>? ignore)
    {
        _env = env;
        _collision = collision;
        _ignore = ignore;
        InitializeComponent();

        var model = CollisionDetail.Build(env, collision, ignore);
        RenderHeader(model);
        RenderTable(model);
    }

    private void RenderHeader(CollisionDetail.Model model)
    {
        TxtTitle.Text = $"{Capitalize(model.TypeLabel)}: {model.ObjName}";

        if (model.FriendlyName is { Length: > 0 } friendly)
        {
            TxtFriendly.Text = $"“{friendly}”";
            TxtFriendly.Foreground = ThemeManager.Dim;
        }
        else TxtFriendly.Visibility = Visibility.Collapsed;

        var mods = model.HasVanilla ? model.Columns.Count - 1 : model.Columns.Count;
        var sub = model.HasVanilla
            ? $"Vanilla and {mods} mod version(s), side by side. Highlighted = changed from vanilla."
            : $"{mods} mod version(s) side by side (this object has no vanilla version). Highlighted = a mod set the field.";
        if (model.MissingClaimants.Count > 0)
            sub += $"  Not found on disk for: {string.Join(", ", model.MissingClaimants)}.";
        TxtSubtitle.Text = sub;
        TxtSubtitle.Foreground = ThemeManager.Dim;

        TxtLegend.Inlines.Clear();
        TxtLegend.Inlines.Add(new System.Windows.Documents.Run("changed  ") { Foreground = ThemeManager.Warn });
        TxtLegend.Inlines.Add(new System.Windows.Documents.Run("disagree") { Foreground = ThemeManager.Bad });
    }

    private void RenderTable(CollisionDetail.Model model)
    {
        var grid = TableHost;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // field label
        foreach (var _ in model.Columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // header
        AddCell(grid, 0, 0, HeaderText("Field"));
        for (var col = 0; col < model.Columns.Count; col++)
        {
            var isVanilla = model.HasVanilla && col == 0;
            AddCell(grid, col + 1, 0, HeaderText(model.Columns[col], isVanilla ? ThemeManager.Dim : ThemeManager.Normal));
        }

        var r = 1;
        foreach (var row in model.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelColor = row.Contested ? ThemeManager.Bad : row.AnyChanged ? ThemeManager.Normal : ThemeManager.Dim;
            var label = new TextBlock
            {
                Text = row.Field,
                FontFamily = Mono,
                FontSize = 12,
                Foreground = labelColor,
                FontWeight = row.Contested ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
            };
            AddCell(grid, 0, r, label);

            for (var col = 0; col < row.Cells.Count; col++)
            {
                var isVanilla = model.HasVanilla && col == 0;
                AddCell(grid, col + 1, r, ValueCell(row.Cells[col], row.Contested, isVanilla));
            }
            r++;
        }
    }

    // ------------------------------------------------------------- rendering ---

    private static TextBlock HeaderText(string text) => HeaderText(text, ThemeManager.Normal);

    private static TextBlock HeaderText(string text, Brush color) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = color,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A value cell: the readable value, tinted when it changed (amber) or disagrees (red).</summary>
    private static Border ValueCell(CollisionDetail.Cell cell, bool contestedRow, bool isVanilla)
    {
        Brush? bg = null, bd = null;
        if (cell.Changed && !isVanilla)
        {
            var alarm = contestedRow;
            bg = alarm ? ThemeManager.BannerAlarmBg : ThemeManager.BannerInfoBg;
            bd = alarm ? ThemeManager.BannerAlarmBorder : ThemeManager.BannerInfoBorder;
        }

        return new Border
        {
            Background = bg ?? Brushes.Transparent,
            BorderBrush = bd ?? ThemeManager.PanelBorder,
            BorderThickness = new Thickness(bd is null ? 0 : 1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 4),
            MinWidth = 150,
            MaxWidth = 300,
            Child = ValueVisual(cell, isVanilla),
        };
    }

    private static UIElement ValueVisual(CollisionDetail.Cell cell, bool isVanilla)
    {
        if (!cell.Present)
            return new TextBlock
            {
                Text = isVanilla ? "(not in vanilla)" : "(absent)",
                Foreground = ThemeManager.Dim,
                FontStyle = FontStyles.Italic,
            };

        var node = cell.Node;
        if (node is JsonArray arr)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = $"{arr.Count} entr{(arr.Count == 1 ? "y" : "ies")}",
                Foreground = ThemeManager.Dim,
            });
            if (arr.Count > 0)
            {
                var shown = arr.Take(5).Select(ElemText);
                var tb = new TextBlock
                {
                    Text = string.Join("\n", shown) + (arr.Count > 5 ? $"\n(+{arr.Count - 5} more)" : ""),
                    FontFamily = Mono,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 0),
                };
                if (arr.Count > 5) tb.ToolTip = string.Join("\n", arr.Select(ElemText));
                panel.Children.Add(tb);
            }
            return panel;
        }
        if (node is JsonObject o)
            return new TextBlock { Text = $"{{{o.Count} field(s)}}", Foreground = ThemeManager.Dim, TextWrapping = TextWrapping.Wrap };

        return new TextBlock
        {
            Text = Unquote(node),
            FontFamily = Mono,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static string ElemText(JsonNode? n)
    {
        if (n is null) return "null";
        var s = n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();
        return s.Length > 80 ? s[..77] + "…" : s;
    }

    private static string Unquote(JsonNode? n) =>
        n is null ? "null"
        : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();

    private static void AddCell(Grid grid, int col, int row, UIElement child)
    {
        Grid.SetColumn(child, col);
        Grid.SetRow(child, row);
        if (child is FrameworkElement fe && fe is not Border)
            fe.Margin = new Thickness(0, 3, 12, 4);
        grid.Children.Add(child);
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---------------------------------------------------------------- copy ---

    private void CopyJson_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        void Dump(string label, JsonObject? obj)
        {
            sb.AppendLine($"// {label}");
            sb.AppendLine(obj is null ? "(not found)" : obj.ToJsonString(Indented));
            sb.AppendLine();
        }

        var core = FieldDiff.LoadObject(Path.Combine(_env.CoreDataDir, _collision.Type), _collision.ObjName, _ignore) as JsonObject;
        if (core is not null) Dump("Vanilla", core);
        foreach (var m in _collision.Claimants)
        {
            var obj = m.Dir is null ? null
                : FieldDiff.LoadObject(Path.Combine(m.Dir, "data", _collision.Type), _collision.ObjName, _ignore) as JsonObject;
            Dump(m.DisplayName ?? m.Name, obj);
        }

        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard busy - not fatal */ }
    }
}
