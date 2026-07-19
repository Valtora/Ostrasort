using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostrasort.Gui;

/// <summary>
/// The conflict resolver as a paged wizard: a summary of what conflicts and
/// what merges by itself, then one decision per page (with a pre-selected
/// suggestion so Continue is never blocked), then a review page that creates
/// the patch. "Choose for me" accepts the suggestion for everything remaining -
/// the same outcome the headless fallback produces, marked for later review.
/// The passed MergePlan is mutated in place; OK/Cancel tells the caller
/// whether to generate from it.
/// </summary>
public partial class ResolverDialog : Window
{
    private static readonly FontFamily Mono = new("Consolas");

    /// <summary>One decision page: the item it decides, its pre-built UI, and its radios (for sync).</summary>
    private sealed record DecisionPage(MergeItem Item, string SuggestedId, UIElement Content, List<RadioButton> Radios);

    private readonly MergePlan _plan;
    private readonly List<DecisionPage> _pages = new();
    private int _index = -1;   // -1 = summary, 0.._pages.Count-1 = decisions, _pages.Count = review

    public ResolverDialog(MergePlan plan)
    {
        _plan = plan;
        InitializeComponent();
        TxtProgress.Foreground = ThemeManager.Dim;

        // pre-select a suggestion on every undecided item: the later-loaded
        // MOD's value, exactly what the game itself (and the headless fallback)
        // would produce. AutoResolved marks it "a suggestion, not a person's
        // choice" until the user confirms the page or picks something.
        foreach (var item in _plan.Unresolved.ToList())
        {
            var pick = item.Options.LastOrDefault(o => o.SourceId != "__union__") ?? item.Options[^1];
            item.ChosenSourceId = pick.SourceId;
            item.AutoResolved = true;
        }

        BuildDecisionPages();
        ShowPage(-1);
    }

    // ---------------------------------------------------------------- pages ---

    private void BuildDecisionPages()
    {
        foreach (var pool in _plan.Pools)
            foreach (var item in pool.AllItems.Where(i => i.Contested))
                _pages.Add(BuildDecisionPage(pool.Collision, item, isLoot: true));
        foreach (var obj in _plan.Objects)
            foreach (var item in obj.Fields.Where(i => i.Contested))
                _pages.Add(BuildDecisionPage(obj.Collision, item, isLoot: false));
    }

    private void ShowPage(int index)
    {
        _index = index;
        var total = _pages.Count;

        if (index < 0)
        {
            TxtPageTitle.Text = "What conflicts, and what happens next";
            TxtProgress.Text = total == 0 ? "" : $"{total} decision(s) ahead";
            PageHost.Content = BuildSummary();
        }
        else if (index < total)
        {
            var page = _pages[index];
            TxtPageTitle.Text = "Your decision";
            TxtProgress.Text = $"Decision {index + 1} of {total}";
            SyncRadios(page);
            PageHost.Content = page.Content;
        }
        else
        {
            TxtPageTitle.Text = "Review and create the patch";
            TxtProgress.Text = "Review";
            PageHost.Content = BuildReview();
        }

        BtnBack.Visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.Visibility = index < total ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.Content = index < 0 ? "Continue" : index == total - 1 ? "Continue to review" : "Continue";
        BtnOk.Visibility = index >= total ? Visibility.Visible : Visibility.Collapsed;
        BtnOk.IsEnabled = !_plan.Unresolved.Any();
        BtnChooseForMe.Visibility = index < total && total > 0 && _pages.Skip(Math.Max(0, index)).Any()
            ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowPage(_index - 1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        // seeing the page and continuing IS the confirmation - the suggestion
        // becomes the user's decision and stops being flagged for review
        if (_index >= 0 && _index < _pages.Count)
            _pages[_index].Item.AutoResolved = false;
        ShowPage(_index + 1);
    }

    private void ChooseForMe_Click(object sender, RoutedEventArgs e) => ShowPage(_pages.Count);

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    // -------------------------------------------------------------- summary ---

    private UIElement BuildSummary()
    {
        var panel = new StackPanel();
        var units = _plan.Pools.Count + _plan.Objects.Count;
        var auto = _plan.AllItems.Count(i => !i.Contested);
        var contested = _pages.Count;

        Para(panel,
            $"{units} thing(s) are changed by more than one mod. " +
            $"{auto} change(s) combine automatically with nothing lost. " +
            (contested == 0 ? "Nothing needs a decision."
                : contested == 1 ? "1 needs you to choose." : $"{contested} need you to choose."),
            ThemeManager.Normal, 13.5, semiBold: true);
        Para(panel,
            "Ostrasort will create a small extra mod (the compatibility patch) that loads after the mods " +
            "it merges, so no mod's changes are lost. Every decision is remembered for future refreshes, " +
            "and the patch can be removed at any time.", ThemeManager.Dim);

        foreach (var pool in _plan.Pools) SummaryUnit(panel, pool.Collision, pool.AllItems.ToList(), isLoot: true);
        foreach (var obj in _plan.Objects) SummaryUnit(panel, obj.Collision, obj.Fields, isLoot: false);

        if (contested > 0)
            Para(panel,
                "Each decision already has a suggested pick (the later-loaded mod's value, the same outcome " +
                "the game itself would produce). Continue to confirm them one by one, or press Choose for me.",
                ThemeManager.Dim);
        return panel;
    }

    private void SummaryUnit(Panel panel, Collision c, IReadOnlyList<MergeItem> items, bool isLoot)
    {
        var contested = items.Count(i => i.Contested);
        var auto = items.Count - contested;
        var head = c.FriendlyName is { Length: > 0 } f ? $"“{f}” ({c.ObjName})" : c.ObjName;
        var line = $"• {Capitalize(CollisionView.TypeLabel(c.Type))} {head}, "
                   + (isLoot ? "stocked by " : "edited by ") + ModList(c) + ": "
                   + (auto > 0 ? $"{auto} merge(s) automatically" : "")
                   + (auto > 0 && contested > 0 ? ", " : "")
                   + (contested > 0 ? $"{contested} to decide" : "")
                   + ".";
        Para(panel, line, ThemeManager.Normal, margin: new Thickness(0, 2, 0, 2));
    }

    // ------------------------------------------------------- decision pages ---

    private DecisionPage BuildDecisionPage(Collision c, MergeItem item, bool isLoot)
    {
        var suggestedId = item.ChosenSourceId
            ?? (item.Options.LastOrDefault(o => o.SourceId != "__union__") ?? item.Options[^1]).SourceId;

        var panel = new StackPanel();

        // context: what object, whose conflict
        var head = c.FriendlyName is { Length: > 0 } f ? $"“{f}” ({c.ObjName})" : c.ObjName;
        Para(panel,
            isLoot
                ? $"Both mods stock “{item.Token}” in the {CollisionView.TypeLabel(c.Type)} {head}."
                : $"Both mods change “{FriendlyField(item.Token)}” of the {CollisionView.TypeLabel(c.Type)} {head}.",
            ThemeManager.Normal, 13.5, semiBold: true);
        Para(panel, (isLoot ? "Stocked by " : "Edited by ") + ModList(c) + ". Which should the game use?",
            ThemeManager.Dim);
        if (!isLoot && !string.Equals(FriendlyField(item.Token), item.Token, StringComparison.Ordinal))
            Para(panel, $"(the raw field is {item.Token})", ThemeManager.Dim, 11);
        if (item.AutoResolved && item.ChosenSourceId is not null)
            Para(panel, "A pick from an earlier run (or the suggestion) is pre-selected. Continue keeps it.",
                ThemeManager.Dim, 11.5);

        var columns = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var group = $"d{_pages.Count}:{item.Token}";
        var radios = new List<RadioButton>();

        // objects offer the vanilla baseline first (choosing it reverts the
        // field - the model's "exclude"); loot has no vanilla base
        if (!isLoot)
            columns.Children.Add(BaselineColumn(group, item, radios));
        foreach (var opt in item.Options)
            columns.Children.Add(OptionColumn(group, item, opt, isLoot, radios, opt.SourceId == suggestedId));
        if (isLoot)
            columns.Children.Add(ExcludeColumn(group, item, radios));

        panel.Children.Add(columns);
        return new DecisionPage(item, suggestedId, panel, radios);
    }

    /// <summary>Re-check the page's radios from the item's current state (prior page edits, Choose for me).</summary>
    private static void SyncRadios(DecisionPage page)
    {
        foreach (var rb in page.Radios)
            rb.IsChecked = rb.Tag is MergeOption o
                ? !page.Item.Excluded && page.Item.ChosenSourceId == o.SourceId
                : page.Item.Excluded;
    }

    /// <summary>A selectable column card: a radio the whole card toggles, a heading, and the readable value.</summary>
    private static Border ColumnShell(RadioButton rb, string heading, Brush headingColor, UIElement valueVisual, bool suggested = false)
    {
        var content = new StackPanel();
        var headRow = new WrapPanel();
        headRow.Children.Add(new TextBlock
        {
            Text = heading,
            FontWeight = FontWeights.SemiBold,
            Foreground = headingColor,
            Margin = new Thickness(0, 0, 0, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (suggested)
            headRow.Children.Add(new TextBlock
            {
                Text = "  (suggested)",
                Foreground = ThemeManager.Good,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            });
        content.Children.Add(headRow);
        content.Children.Add(valueVisual);

        rb.Content = content;
        rb.HorizontalAlignment = HorizontalAlignment.Stretch;
        rb.HorizontalContentAlignment = HorizontalAlignment.Left;
        rb.VerticalContentAlignment = VerticalAlignment.Top;

        return new Border
        {
            BorderBrush = suggested ? ThemeManager.Good : ThemeManager.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 8, 8),
            MinWidth = 190,
            MaxWidth = 340,
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
        };
        rb.ToolTip = "Do not sell/spawn this item at all - it is dropped from the merged pool.";
        radios.Add(rb);
        return ColumnShell(rb, "Don't stock this item", ThemeManager.Bad,
            new TextBlock { Text = "dropped from the merged pool", Foreground = ThemeManager.Dim, TextWrapping = TextWrapping.Wrap });
    }

    private Border OptionColumn(string group, MergeItem item, MergeOption opt, bool isLoot, List<RadioButton> radios, bool suggested)
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
        };
        var heading = isUnion ? "Union of both" : opt.SourceLabel;
        var color = isUnion ? ThemeManager.Good : ThemeManager.Normal;
        if (isUnion) rb.ToolTip = "Keep every entry either mod has - nothing is dropped.";
        radios.Add(rb);
        return ColumnShell(rb, heading, color, ValueForOption(opt, item, isLoot), suggested);
    }

    // --------------------------------------------------------------- review ---

    private UIElement BuildReview()
    {
        var panel = new StackPanel();
        Para(panel, "Everything is decided. Here is what the compatibility patch will contain.",
            ThemeManager.Normal, 13.5, semiBold: true);

        foreach (var pool in _plan.Pools)
            ReviewUnit(panel, pool.Collision, pool.AllItems.ToList(), isLoot: true);
        foreach (var obj in _plan.Objects)
            ReviewUnit(panel, obj.Collision, obj.Fields, isLoot: false);

        Para(panel,
            "The patch is a small extra mod (“Ostrasort Patch”) that loads after the mods it merges, " +
            "so nothing is lost. Merged game objects are best-effort, so verify them in game. If something " +
            "looks odd, remove the patch any time (the Compatibility patch tab, or the More menu).",
            ThemeManager.Dim);
        return panel;
    }

    private void ReviewUnit(Panel panel, Collision c, IReadOnlyList<MergeItem> items, bool isLoot)
    {
        var head = new TextBlock
        {
            Text = $"{Capitalize(CollisionView.TypeLabel(c.Type))}: {c.ObjName}"
                   + (c.FriendlyName is { Length: > 0 } f ? $"  “{f}”" : ""),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 3),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(head);

        foreach (var item in items.Where(i => i.Contested))
        {
            var label = isLoot ? item.Token : FriendlyField(item.Token);
            var outcome = item.Excluded
                ? (isLoot ? "not stocked" : "kept at the vanilla value")
                : item.Resolved.SourceId == "__union__" ? "union of both mods"
                : $"{item.Resolved.SourceLabel}'s value";
            var flag = item.AutoResolved ? "  (suggested pick, review in game)" : "";
            Para(panel, $"• {label}: {outcome}{flag}",
                item.AutoResolved ? ThemeManager.Warn : ThemeManager.Normal,
                margin: new Thickness(12, 1, 0, 1));
        }

        var carried = items.Where(i => !i.Contested).ToList();
        if (carried.Count > 0)
        {
            var exp = CarriedExpander(carried, isLoot);
            exp.Margin = new Thickness(12, 2, 0, 2);
            panel.Children.Add(exp);
        }
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
            return $"stocks {parts[1]}  (chance weight {parts[0]})";
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
                               : (opt.Node is null ? "(removed)" : ShortValue(opt.Node));
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
            cb.Checked += (_, _) => item.Excluded = false;
            cb.Unchecked += (_, _) => item.Excluded = true;
            list.Children.Add(cb);
        }
        return new Expander
        {
            Header = isLoot
                ? $"{carried.Count} uncontested item(s) carried over - expand to review or exclude"
                : $"{carried.Count} field(s) merged automatically - expand to review or revert to vanilla",
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

    private static string ShortValue(JsonNode node)
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

    private static string ModList(Collision c) =>
        Join(c.Claimants.Select(m => m.DisplayName ?? m.Name).ToList());

    private static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? (names.Count == 1 ? names[0] : "")
        : names.Count == 2 ? $"{names[0]} and {names[1]}"
        : string.Join(", ", names.Take(names.Count - 1)) + $", and {names[^1]}";

    private static void Para(Panel panel, string text, Brush brush, double size = 12.5,
                             bool semiBold = false, Thickness? margin = null)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0, 0, 0, 8),
        });
    }

    // --------------------------------------------------------------- testing ---

    /// <summary>
    /// Test hook: count the RadioButtons across every pre-built decision page
    /// (pages are built eagerly; only one is shown at a time). A contested plan
    /// must yield selectors here - if it renders zero, the resolver is broken
    /// (this exact regression shipped once).
    /// </summary>
    internal int SelectorsInTree() => _pages.Sum(p => p.Radios.Count);
}
