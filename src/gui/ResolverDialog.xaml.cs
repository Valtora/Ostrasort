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
            var carried = pool.AllItems.Count(i => !i.Contested);

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
                                item.AutoResolved = false;
                            }
                            SyncRadios();
                            UpdateButtons();
                        };
                        shortcuts.Children.Add(btn);
                    }
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
                            IsChecked = item.ChosenSourceId == opt.SourceId,
                        };
                        rb.Checked += (_, _) =>
                        {
                            item.ChosenSourceId = opt.SourceId;
                            item.AutoResolved = false;   // a human decided
                            UpdateButtons();
                        };
                        radios.Children.Add(rb);
                        wiredRadios.Add(rb);
                    }
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

            if (carried > 0)
                body.Children.Add(new TextBlock
                {
                    Text = $"+ {carried} uncontested item(s) carried over automatically",
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 6, 0, 0),
                });

            PoolsHost.Children.Add(box);
        }
    }

    /// <summary>Re-sync radio checks after a take-all shortcut changed the model.</summary>
    private void SyncRadios()
    {
        foreach (var (item, radios) in _wired)
            foreach (var rb in radios)
                rb.IsChecked = item.ChosenSourceId == ((MergeOption)rb.Tag).SourceId;
    }

    private void UpdateButtons()
    {
        var remaining = _plan.Unresolved.Count();
        BtnOk.IsEnabled = remaining == 0;
        TxtRemaining.Text = remaining == 0 ? "all items decided" : $"{remaining} item(s) still undecided";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
