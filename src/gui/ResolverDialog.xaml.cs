using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostrasort.Gui;

public partial class ResolverDialog : Window
{
    private readonly MergePlan _plan;
    private readonly List<(MergeItem Item, List<RadioButton> Radios)> _wired = new();
    private static readonly FontFamily Mono = new("Consolas");

    public ResolverDialog(MergePlan plan)
    {
        _plan = plan;
        InitializeComponent();
        TxtIntro.Foreground = ThemeManager.Dim;
        TxtRemaining.Foreground = ThemeManager.Dim;
        Build();
        UpdateButtons();
    }

    private void Build()
    {
        var unit = 0;
        foreach (var pool in _plan.Pools)
            AddUnit(++unit, "Shop pool", pool.Collision, pool.AllItems.ToList(), isLoot: true);

        foreach (var obj in _plan.Objects)
            AddUnit(++unit, Capitalize(CollisionView.TypeLabel(obj.Type)), obj.Collision, obj.Fields, isLoot: false);
    }

    private static string ModList(Collision c) =>
        Join(c.Claimants.Select(m => m.DisplayName ?? m.Name).ToList());

    /// <summary>Renders one merge unit (a loot pool or an object) as a titled card with per-field decisions.</summary>
    private void AddUnit(int unitId, string typeLabel, Collision c, IReadOnlyList<MergeItem> items, bool isLoot)
    {
        var body = new StackPanel();

        // friendly header: a bold title plus a dim "edited by A and B" subtitle,
        // instead of a single raw string with the internal type slug in it
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock
        {
            Text = $"{typeLabel}: {c.ObjName}",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });
        if (c.FriendlyName is { Length: > 0 } friendly)
            head.Children.Add(new TextBlock
            {
                Text = $"“{friendly}”",
                Foreground = ThemeManager.Dim,
                Margin = new Thickness(0, 1, 0, 0),
            });
        head.Children.Add(new TextBlock
        {
            Text = (isLoot ? "stocked by " : "edited by ") + ModList(c),
            Foreground = ThemeManager.Dim,
            Margin = new Thickness(0, 1, 0, 0),
        });

        var box = new GroupBox
        {
            Header = head,
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(10),
            Content = body,
        };

        var contested = items.Where(i => i.Contested).ToList();
        var carried = items.Where(i => !i.Contested).ToList();

        if (contested.Count > 0)
        {
            AddTakeAllShortcuts(body, contested, isLoot);
            foreach (var item in contested)
                body.Children.Add(FieldCard(unitId, item, isLoot));
        }

        if (carried.Count > 0)
            body.Children.Add(CarriedExpander(carried, isLoot));

        PoolsHost.Children.Add(box);
    }

    /// <summary>"Take all from &lt;mod&gt;" buttons - one per source that appears in any contested row.</summary>
    private void AddTakeAllShortcuts(Panel body, List<MergeItem> contested, bool isLoot)
    {
        var sources = contested.SelectMany(i => i.Options)
            .Where(o => o.SourceId != "__union__")
            .GroupBy(o => o.SourceId).Select(g => g.First()).ToList();
        if (sources.Count <= 1) return;

        var shortcuts = new WrapPanel { Margin = new Thickness(0, 2, 0, 10) };
        shortcuts.Children.Add(new TextBlock
        {
            Text = "Take all from:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = ThemeManager.Dim,
        });
        foreach (var src in sources)
        {
            var btn = new Button { Content = src.SourceLabel, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            btn.Click += (_, _) =>
            {
                foreach (var item in contested.Where(i => i.Options.Any(o => o.SourceId == src.SourceId)))
                {
                    item.ChosenSourceId = src.SourceId;
                    item.Excluded = false;
                    item.AutoResolved = false;
                }
                SyncRadios();
                UpdateButtons();
            };
            shortcuts.Children.Add(btn);
        }
        shortcuts.Children.Add(new TextBlock
        {
            Text = isLoot ? "or decide each item below"
                          : "or decide each field below",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeManager.Dim,
            Margin = new Thickness(6, 0, 0, 0),
        });
        body.Children.Add(shortcuts);
    }

    /// <summary>One contested decision rendered as a card: the field/item, then side-by-side option columns.</summary>
    private Border FieldCard(int unitId, MergeItem item, bool isLoot)
    {
        var card = new StackPanel();

        // title row: friendly label (bold) + the raw token in dim monospace
        var title = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        title.Children.Add(new TextBlock
        {
            Text = isLoot ? item.Token : FriendlyField(item.Token),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!isLoot && !string.Equals(FriendlyField(item.Token), item.Token, StringComparison.Ordinal))
            title.Children.Add(new TextBlock
            {
                Text = $"  {item.Token}",
                FontFamily = Mono,
                FontSize = 11,
                Foreground = ThemeManager.Dim,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        if (item.AutoResolved)
            title.Children.Add(new TextBlock
            {
                Text = "   (auto-picked earlier - review)",
                Foreground = ThemeManager.Warn,
                VerticalAlignment = VerticalAlignment.Center,
            });
        card.Children.Add(title);

        // the option columns, wrapping on a narrow window
        var columns = new WrapPanel();
        var group = $"u{unitId}:{item.Token}";
        var radios = new List<RadioButton>();

        // objects show the vanilla baseline first as its own column (choosing it
        // reverts the field - the model's "exclude"); loot has no vanilla base
        if (!isLoot)
            columns.Children.Add(BaselineColumn(group, item, radios));

        foreach (var opt in item.Options)
            columns.Children.Add(OptionColumn(group, item, opt, isLoot, radios));

        // loot: an explicit "stock from nobody" column (the model's "exclude")
        if (isLoot)
            columns.Children.Add(ExcludeColumn(group, item, radios));

        card.Children.Add(columns);
        _wired.Add((item, radios));

        return new Border
        {
            BorderBrush = ThemeManager.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = card,
        };
    }

    /// <summary>A selectable column card: a radio the whole card toggles, a heading, and the readable value.</summary>
    private static Border ColumnShell(RadioButton rb, string heading, Brush headingColor, UIElement valueVisual)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = heading,
            FontWeight = FontWeights.SemiBold,
            Foreground = headingColor,
            Margin = new Thickness(0, 0, 0, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(valueVisual);

        rb.Content = content;
        rb.HorizontalAlignment = HorizontalAlignment.Stretch;
        rb.HorizontalContentAlignment = HorizontalAlignment.Left;
        rb.VerticalContentAlignment = VerticalAlignment.Top;

        return new Border
        {
            BorderBrush = ThemeManager.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 8, 8),
            MinWidth = 190,
            MaxWidth = 320,
            Child = rb,
        };
    }

    private Border BaselineColumn(string group, MergeItem item, List<RadioButton> radios)
    {
        var rb = new RadioButton { GroupName = group, Tag = null, IsChecked = item.Excluded };
        rb.Checked += (_, _) =>
        {
            item.Excluded = true;
            item.ChosenSourceId = null;
            item.AutoResolved = false;
            UpdateButtons();
        };
        rb.ToolTip = "Keep the base game's value for this field, ignoring both mods.";
        radios.Add(rb);
        return ColumnShell(rb, "Vanilla (unchanged)", ThemeManager.Dim, ValueVisual(item.BaseNode, item, isBase: true));
    }

    private Border ExcludeColumn(string group, MergeItem item, List<RadioButton> radios)
    {
        var rb = new RadioButton { GroupName = group, Tag = null, IsChecked = item.Excluded };
        rb.Checked += (_, _) =>
        {
            item.Excluded = true;
            item.ChosenSourceId = null;
            item.AutoResolved = false;
            UpdateButtons();
        };
        rb.ToolTip = "Stock this item from nobody - dropped from the merged pool.";
        radios.Add(rb);
        return ColumnShell(rb, "Stock from nobody", ThemeManager.Bad,
            new TextBlock { Text = "dropped from the pool", Foreground = ThemeManager.Dim, TextWrapping = TextWrapping.Wrap });
    }

    private Border OptionColumn(string group, MergeItem item, MergeOption opt, bool isLoot, List<RadioButton> radios)
    {
        var isUnion = opt.SourceId == "__union__";
        var rb = new RadioButton
        {
            GroupName = group,
            Tag = opt,
            IsChecked = !item.Excluded && item.ChosenSourceId == opt.SourceId,
        };
        rb.Checked += (_, _) =>
        {
            item.ChosenSourceId = opt.SourceId;
            item.Excluded = false;
            item.AutoResolved = false;
            UpdateButtons();
        };
        var heading = isUnion ? "Union of both" : opt.SourceLabel;
        var color = isUnion ? ThemeManager.Good : ThemeManager.Normal;
        return ColumnShell(rb, heading, color, ValueForOption(opt, item, isLoot));
    }

    // ------------------------------------------------------- value rendering ---

    /// <summary>Readable rendering of one option's value (scalar, array-diff, or loot entry).</summary>
    private UIElement ValueForOption(MergeOption opt, MergeItem item, bool isLoot)
    {
        if (isLoot)
            return new TextBlock { Text = LootEntryPretty(opt.Entry), Foreground = ThemeManager.Dim, TextWrapping = TextWrapping.Wrap };
        return ValueVisual(opt.Node, item, isBase: false);
    }

    /// <summary>Renders an object-field value: array fields as a diff vs vanilla, scalars unquoted.</summary>
    private UIElement ValueVisual(JsonNode? node, MergeItem item, bool isBase)
    {
        if (node is null)
            return new TextBlock { Text = isBase ? "(not in vanilla)" : "(removed)", Foreground = ThemeManager.Dim, FontStyle = FontStyles.Italic };

        if (item.IsArrayField && node is JsonArray arr)
            return isBase ? ArrayPlain(arr) : ArrayDiff(arr, item.BaseNode as JsonArray);

        if (node is JsonArray plain) return ArrayPlain(plain);
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

    private static UIElement ArrayPlain(JsonArray arr)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = $"{arr.Count} entr{(arr.Count == 1 ? "y" : "ies")}", Foreground = ThemeManager.Dim });
        if (arr.Count > 0)
            panel.Children.Add(SampleBlock(arr.ToList(), ThemeManager.Normal));
        return panel;
    }

    /// <summary>Shows what this option adds/removes relative to the vanilla array - the actual disagreement.</summary>
    private static UIElement ArrayDiff(JsonArray value, JsonArray? baseArr)
    {
        var baseSet = new HashSet<string>((baseArr ?? new JsonArray()).Select(ElemKey), StringComparer.Ordinal);
        var valSet = new HashSet<string>(value.Select(ElemKey), StringComparer.Ordinal);
        var added = value.Where(e => !baseSet.Contains(ElemKey(e))).ToList();
        var removed = (baseArr ?? new JsonArray()).Where(e => !valSet.Contains(ElemKey(e))).ToList();

        var panel = new StackPanel();
        if (baseArr is null)
        {
            panel.Children.Add(new TextBlock { Text = $"sets {value.Count} entr{(value.Count == 1 ? "y" : "ies")}", Foreground = ThemeManager.Dim });
            if (value.Count > 0) panel.Children.Add(SampleBlock(value.ToList(), ThemeManager.Normal));
            return panel;
        }
        if (added.Count == 0 && removed.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "same as vanilla", Foreground = ThemeManager.Dim, FontStyle = FontStyles.Italic });
            return panel;
        }
        if (added.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = $"+ adds {added.Count}", Foreground = ThemeManager.Good, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(SampleBlock(added, ThemeManager.Good));
        }
        if (removed.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = $"- removes {removed.Count}", Foreground = ThemeManager.Bad, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) });
            panel.Children.Add(SampleBlock(removed, ThemeManager.Bad));
        }
        return panel;
    }

    private static TextBlock SampleBlock(IReadOnlyList<JsonNode?> elems, Brush color, int n = 4)
    {
        var shown = elems.Take(n).Select(ElemText);
        var text = string.Join("\n", shown) + (elems.Count > n ? $"\n(+{elems.Count - n} more)" : "");
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = Mono,
            FontSize = 11,
            Foreground = color,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 0),
        };
        if (elems.Count > n)
            tb.ToolTip = string.Join("\n", elems.Select(ElemText));
        return tb;
    }

    private static string ElemKey(JsonNode? n) => n?.ToJsonString() ?? "null";

    private static string ElemText(JsonNode? n)
    {
        if (n is null) return "null";
        var s = n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();
        return s.Length > 60 ? s[..57] + "…" : s;
    }

    private static string Unquote(JsonNode n) =>
        n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();

    /// <summary>Turns a loot entry ("Item=1.0x21") into a plain phrase for the value cell.</summary>
    private static string LootEntryPretty(string entry)
    {
        var val = entry.Contains('=') ? entry[(entry.IndexOf('=') + 1)..] : entry;
        var parts = val.Split('x');
        if (parts.Length == 2)
            return $"count {parts[1]}  (weight {parts[0]})";
        return val;
    }

    // ---------------------------------------------------- carried / friendly ---

    private Expander CarriedExpander(List<MergeItem> carried, bool isLoot)
    {
        var list = new StackPanel();
        foreach (var item in carried)
        {
            var opt = item.Resolved;
            var label = isLoot ? item.Token : FriendlyField(item.Token);
            var value = isLoot ? LootEntryPretty(opt.Entry)
                               : (opt.Node is null ? "(removed)" : ShortValue(opt.Node, item));
            var cb = new CheckBox
            {
                IsChecked = !item.Excluded,
                Margin = new Thickness(0, 2, 0, 2),
                Content = new TextBlock
                {
                    Text = $"{label}   ({opt.SourceLabel}: {value})",
                    TextWrapping = TextWrapping.Wrap,
                },
                ToolTip = isLoot ? "Untick to exclude this item from the merged pool."
                                 : "Untick to revert this field to the vanilla value.",
            };
            cb.Checked += (_, _) => { item.Excluded = false; UpdateButtons(); };
            cb.Unchecked += (_, _) => { item.Excluded = true; UpdateButtons(); };
            list.Children.Add(cb);
        }
        return new Expander
        {
            Header = isLoot
                ? $"{carried.Count} uncontested item(s) carried over - expand to review or exclude"
                : $"{carried.Count} field(s) auto-merged - expand to review or revert to vanilla",
            Margin = new Thickness(0, 4, 0, 0),
            Content = new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = list,
                Margin = new Thickness(18, 4, 0, 0),
            },
        };
    }

    private static string ShortValue(JsonNode node, MergeItem item)
    {
        if (node is JsonArray a) return $"{a.Count} entr{(a.Count == 1 ? "y" : "ies")}";
        if (node is JsonObject o) return $"{{{o.Count} field(s)}}";
        return Unquote(node);
    }

    // curated labels for the fields modders touch most; everything else is
    // prettified from its Hungarian-prefixed name (strNameFriendly -> "Name Friendly")
    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.Ordinal)
    {
        ["strNameFriendly"] = "Display name",
        ["strDesc"] = "Description",
        ["strColor"] = "Colour",
        ["strCategory"] = "Category",
        ["strBuildType"] = "Build category",
        ["aLoots"] = "Shop stock",
        ["aCOs"] = "Spawned contents",
        ["aStartingConds"] = "Starting conditions",
        ["aStartInstall"] = "Installed form",
        ["aLootCOs"] = "Loot objects",
        ["aReqs"] = "Requirements",
        ["aInteractions"] = "Interactions",
        ["mapPoints"] = "Use / interaction points",
        ["fMass"] = "Mass",
        ["fBasePrice"] = "Base price",
        ["nContainerWidth"] = "Container width",
        ["nContainerHeight"] = "Container height",
    };

    private static readonly string[] Prefixes = { "json", "pair", "map", "arr", "str", "a", "n", "b", "f", "e" };

    internal static string FriendlyField(string token)
    {
        if (FieldLabels.TryGetValue(token, out var label)) return label;

        // strip the longest Hungarian prefix that is immediately followed by an
        // upper-case letter (so "aStartingConds" -> "StartingConds", but a name
        // like "name" with no prefix+caps stays as-is)
        var body = token;
        foreach (var p in Prefixes)
            if (token.Length > p.Length && token.StartsWith(p, StringComparison.Ordinal)
                && char.IsUpper(token[p.Length]))
            {
                body = token[p.Length..];
                break;
            }

        // split camelCase into words
        var sb = new StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            if (i > 0 && char.IsUpper(body[i]) && !char.IsUpper(body[i - 1])) sb.Append(' ');
            sb.Append(body[i]);
        }
        var words = sb.ToString().Trim();
        return words.Length == 0 ? token : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? (names.Count == 1 ? names[0] : "")
        : names.Count == 2 ? $"{names[0]} and {names[1]}"
        : string.Join(", ", names.Take(names.Count - 1)) + $", and {names[^1]}";

    // --------------------------------------------------------------- wiring ---

    /// <summary>Re-sync radio checks after a take-all shortcut changed the model.</summary>
    private void SyncRadios()
    {
        foreach (var (item, radios) in _wired)
            foreach (var rb in radios)
                rb.IsChecked = rb.Tag is MergeOption o
                    ? !item.Excluded && item.ChosenSourceId == o.SourceId
                    : item.Excluded;
    }

    private void UpdateButtons()
    {
        var remaining = _plan.Unresolved.Count();
        var excluded = _plan.AllItems.Count(i => i.Excluded);
        BtnOk.IsEnabled = remaining == 0;
        TxtRemaining.Text = (remaining == 0 ? "all decided" : $"{remaining} still undecided")
                          + (excluded > 0 ? $"  ·  {excluded} excluded/vanilla" : "");
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>
    /// Test hook: count the RadioButtons actually attached to the dialog's tree.
    /// A contested plan must yield selectors here - if it renders zero, the
    /// resolver is broken (this exact regression shipped once).
    /// </summary>
    internal int SelectorsInTree()
    {
        var n = 0;
        void Walk(DependencyObject d)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(d))
            {
                if (child is RadioButton) n++;
                if (child is DependencyObject dep) Walk(dep);
            }
        }
        Walk(PoolsHost);
        return n;
    }
}
