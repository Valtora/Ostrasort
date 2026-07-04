using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostrasort.Gui;

public partial class ResolverDialog : Window
{
    private readonly MergePlan _plan;
    private readonly List<(MergeItem Item, List<RadioButton> Radios)> _wired = new();

    public ResolverDialog(MergePlan plan)
    {
        _plan = plan;
        InitializeComponent();
        Build();
        UpdateButtons();
    }

    private void Build()
    {
        var unit = 0;
        foreach (var pool in _plan.Pools)
            AddUnit(++unit,
                $"Shop pool — {pool.Collision.ObjName}   ({Mods(pool.Collision)})",
                pool.AllItems.ToList(), isLoot: true);

        foreach (var obj in _plan.Objects)
            AddUnit(++unit,
                $"Object — {obj.Collision.ObjName}  ({obj.Type})   ({Mods(obj.Collision)})",
                obj.Fields, isLoot: false);
    }

    private static string Mods(Collision c) =>
        string.Join("  +  ", c.Claimants.Select(m => m.DisplayName ?? m.Name));

    private static string ValueOf(MergeOption opt, bool isLoot) =>
        isLoot && opt.Entry.Contains('=') ? opt.Entry[(opt.Entry.IndexOf('=') + 1)..] : opt.Entry;

    /// <summary>Renders one merge unit (a loot pool or an object) as a group of decisions.</summary>
    private void AddUnit(int unitId, string header, IReadOnlyList<MergeItem> items, bool isLoot)
    {
        var body = new StackPanel();
        var box = new GroupBox
        {
            Header = header,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(8),
            Content = body,
        };

        var contested = items.Where(i => i.Contested).ToList();
        var carried = items.Where(i => !i.Contested).ToList();

        if (contested.Count > 0)
        {
            // take-all shortcuts, one per real mod source that appears in any contested row
            var sources = contested.SelectMany(i => i.Options)
                .Where(o => o.SourceId != "__union__")
                .GroupBy(o => o.SourceId).Select(g => g.First()).ToList();
            if (sources.Count > 1)
            {
                var shortcuts = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                shortcuts.Children.Add(new TextBlock
                {
                    Text = "Take all from:",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Foreground = Brushes.Gray,
                });
                foreach (var src in sources)
                {
                    var btn = new Button { Content = src.SourceLabel, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
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
                    Text = isLoot ? "(or per row: pick a mod's value, or Exclude to stock it from nobody)"
                                  : "(or per row: pick a mod's value / the union, or Exclude to keep vanilla)",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(8, 0, 0, 0),
                });
                body.Children.Add(shortcuts);
            }

            foreach (var item in contested)
                body.Children.Add(ContestedRow(unitId, item, isLoot));
        }

        if (carried.Count > 0)
        {
            var list = new StackPanel();
            foreach (var item in carried)
            {
                var opt = item.Resolved;
                var cb = new CheckBox
                {
                    IsChecked = !item.Excluded,
                    Margin = new Thickness(0, 2, 0, 2),
                    Content = new TextBlock
                    {
                        Text = $"{item.Token}   ({opt.SourceLabel}: {ValueOf(opt, isLoot)})",
                        FontFamily = new FontFamily("Consolas"),
                    },
                    ToolTip = isLoot ? "Untick to exclude this item from the merged pool."
                                     : "Untick to revert this field to the vanilla value.",
                };
                cb.Checked += (_, _) => { item.Excluded = false; UpdateButtons(); };
                cb.Unchecked += (_, _) => { item.Excluded = true; UpdateButtons(); };
                list.Children.Add(cb);
            }
            var expander = new Expander
            {
                Header = isLoot
                    ? $"{carried.Count} uncontested item(s) carried over — expand to review or exclude"
                    : $"{carried.Count} field(s) auto-merged — expand to review or revert to vanilla",
                Margin = new Thickness(0, 6, 0, 0),
                Content = new ScrollViewer
                {
                    MaxHeight = 220,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = list,
                    Margin = new Thickness(18, 4, 0, 0),
                },
            };
            body.Children.Add(expander);
        }

        PoolsHost.Children.Add(box);
    }

    private Grid ContestedRow(int unitId, MergeItem item, bool isLoot)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock
        {
            Text = item.Token,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var radios = new WrapPanel();
        Grid.SetColumn(radios, 1);
        var wired = new List<RadioButton>();
        var group = $"u{unitId}:{item.Token}";
        foreach (var opt in item.Options)
        {
            var rb = new RadioButton
            {
                GroupName = group,
                Content = $"{opt.SourceLabel}:  {ValueOf(opt, isLoot)}",
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
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
            radios.Children.Add(rb);
            wired.Add(rb);
        }

        var exclude = new RadioButton
        {
            GroupName = group,
            Content = isLoot ? "Exclude" : "Vanilla",
            Foreground = Brushes.Firebrick,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = null,
            ToolTip = isLoot ? "Stock this item from nobody - dropped from the merged pool."
                             : "Keep the base game's value for this field, ignoring both mods.",
            IsChecked = item.Excluded,
        };
        exclude.Checked += (_, _) =>
        {
            item.Excluded = true;
            item.ChosenSourceId = null;
            item.AutoResolved = false;
            UpdateButtons();
        };
        radios.Children.Add(exclude);
        wired.Add(exclude);

        if (item.AutoResolved)
            radios.Children.Add(new TextBlock
            {
                Text = "(auto-picked earlier — review)",
                Foreground = Brushes.DarkOrange,
                VerticalAlignment = VerticalAlignment.Center,
            });

        row.Children.Add(radios);
        _wired.Add((item, wired));
        return row;
    }

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
