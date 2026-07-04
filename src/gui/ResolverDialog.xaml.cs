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
        var poolIndex = 0;
        foreach (var pool in _plan.Pools)
        {
            poolIndex++;
            var body = new StackPanel();
            var box = new GroupBox
            {
                Header = $"{pool.Collision.Key}   ({string.Join("  +  ", pool.Collision.Claimants.Select(m => m.DisplayName ?? m.Name))})",
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(8),
                Content = body,
            };

            var contested = pool.AllItems.Where(i => i.Contested).ToList();
            var carried = pool.AllItems.Where(i => !i.Contested).ToList();

            if (contested.Count > 0)
            {
                // take-all shortcuts, one per source that appears in any contested item
                var sources = contested.SelectMany(i => i.Options)
                    .GroupBy(o => o.SourceId)
                    .Select(g => g.First())
                    .ToList();
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
                        Text = "(per row: pick a mod's value, or Exclude to stock it from nobody)",
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(8, 0, 0, 0),
                    });
                    body.Children.Add(shortcuts);
                }

                foreach (var item in contested)
                {
                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    var name = new TextBlock
                    {
                        Text = item.Token,
                        FontFamily = new FontFamily("Consolas"),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    row.Children.Add(name);

                    var radios = new WrapPanel();
                    Grid.SetColumn(radios, 1);
                    var wiredRadios = new List<RadioButton>();
                    foreach (var opt in item.Options)
                    {
                        var value = opt.Entry.Contains('=') ? opt.Entry[(opt.Entry.IndexOf('=') + 1)..] : opt.Entry;
                        var rb = new RadioButton
                        {
                            GroupName = $"p{poolIndex}:{item.Token}",
                            Content = $"{opt.SourceLabel}:  {value}",
                            Margin = new Thickness(0, 0, 16, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Tag = opt,
                            IsChecked = !item.Excluded && item.ChosenSourceId == opt.SourceId,
                        };
                        rb.Checked += (_, _) =>
                        {
                            item.ChosenSourceId = opt.SourceId;
                            item.Excluded = false;
                            item.AutoResolved = false;   // a human decided
                            UpdateButtons();
                        };
                        radios.Children.Add(rb);
                        wiredRadios.Add(rb);
                    }

                    var exclude = new RadioButton
                    {
                        GroupName = $"p{poolIndex}:{item.Token}",
                        Content = "Exclude",
                        Foreground = Brushes.Firebrick,
                        Margin = new Thickness(0, 0, 16, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Tag = null,
                        ToolTip = "Stock this item from nobody - it is dropped from the merged pool entirely.",
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
                    wiredRadios.Add(exclude);

                    if (item.AutoResolved)
                        radios.Children.Add(new TextBlock
                        {
                            Text = "(auto-picked earlier — review)",
                            Foreground = Brushes.DarkOrange,
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                    row.Children.Add(radios);
                    body.Children.Add(row);
                    _wired.Add((item, wiredRadios));
                }
            }

            if (carried.Count > 0)
            {
                var list = new StackPanel();
                foreach (var item in carried)
                {
                    var opt = item.Resolved;
                    var value = opt.Entry.Contains('=') ? opt.Entry[(opt.Entry.IndexOf('=') + 1)..] : opt.Entry;
                    var cb = new CheckBox
                    {
                        IsChecked = !item.Excluded,
                        Margin = new Thickness(0, 2, 0, 2),
                        Content = new TextBlock
                        {
                            Text = $"{item.Token}   ({opt.SourceLabel}: {value})",
                            FontFamily = new FontFamily("Consolas"),
                        },
                        ToolTip = "Untick to exclude this item from the merged pool.",
                    };
                    cb.Checked += (_, _) => { item.Excluded = false; UpdateButtons(); };
                    cb.Unchecked += (_, _) => { item.Excluded = true; UpdateButtons(); };
                    list.Children.Add(cb);
                }
                var expander = new Expander
                {
                    Header = $"{carried.Count} uncontested item(s) carried over — expand to review or exclude",
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
        var excluded = _plan.Pools.SelectMany(p => p.AllItems).Count(i => i.Excluded);
        BtnOk.IsEnabled = remaining == 0;
        TxtRemaining.Text = (remaining == 0 ? "all items decided" : $"{remaining} item(s) still undecided")
                          + (excluded > 0 ? $"  ·  {excluded} excluded" : "");
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
