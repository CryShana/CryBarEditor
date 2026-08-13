using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CryBar.Scenario;
using CryBar.Scenario.Editor;
using CryBar.Scenario.Editor.Commands;
using CryBarEditor.Classes;
using System.Collections.Generic;

namespace CryBarEditor.Controls;

public partial class ScenarioInspectorPanel
{
    public System.Func<System.Threading.Tasks.Task<List<string>?>>? LoadMajorGodNamesAsync;
    public event System.Action? SelectGodBarRequested;

    ScenarioPreviewData? _worldData;
    bool _worldBuilt;
    // Set while a World control commits its own command so the editor-Changed
    // rebuild only fires for undo/redo, not for edits typed into these controls.
    bool _suppressWorldRefresh;

    static readonly IBrush StanceAlly = new SolidColorBrush(Color.Parse("#3f9950"));
    static readonly IBrush StanceNeutral = new SolidColorBrush(Color.Parse("#666666"));
    static readonly IBrush StanceEnemy = new SolidColorBrush(Color.Parse("#a03535"));
    static readonly IBrush StanceSelf = new SolidColorBrush(Color.Parse("#22262e"));

    public void SetWorld(ScenarioPreviewData? data)
    {
        _worldData = data;
        _worldBuilt = false;

        var players = data?.Players;
        _worldSection.IsVisible = players is { Players.Count: > 0 };

        if (_worldSection.IsVisible && _worldContent.IsVisible)
            _ = RebuildWorldAsync();
    }

    void OnWorldToggleClick(object? sender, RoutedEventArgs e)
    {
        var expanded = !_worldContent.IsVisible;
        _worldContent.IsVisible = expanded;
        _worldToggleIcon.Kind = expanded
            ? Material.Icons.MaterialIconKind.ChevronUp
            : Material.Icons.MaterialIconKind.ChevronDown;

        if (expanded && !_worldBuilt)
            _ = RebuildWorldAsync();
    }

    void OnSelectGodBarClick(object? sender, RoutedEventArgs e) => SelectGodBarRequested?.Invoke();

    void RefreshWorldOnEditorChange()
    {
        if (_suppressWorldRefresh) return;

        // Discard() nulls LastChange -> everything may have reverted, rebuild.
        var hint = _boundEditor?.LastChange?.Hint ?? RenderHint.Players;
        if ((hint & RenderHint.Players) == 0) return;

        if (_worldContent.IsVisible && _worldSection.IsVisible)
            _ = RebuildWorldAsync();
    }

    void ExecuteWorldCommand(CryBar.Scenario.Editor.IScenarioCommand? cmd)
    {
        if (cmd is null) return;
        _suppressWorldRefresh = true;
        try { ExecuteCommand?.Invoke(cmd); }
        finally { _suppressWorldRefresh = false; }
    }

    async System.Threading.Tasks.Task RebuildWorldAsync()
    {
        var data = _worldData;
        var view = data?.Players;
        if (data is null || view is null) return;

        List<string>? godNames = data.MajorGodNamesCache;
        if (godNames is null && LoadMajorGodNamesAsync is not null)
            godNames = await LoadMajorGodNamesAsync();

        // Async gap: bail if the scenario changed underneath us.
        if (!ReferenceEquals(data, _worldData)) return;
        _worldBuilt = true;

        _selectGodBarButton.IsVisible = godNames is null;

        var brushes = BuildPlayerBrushes(view);

        _worldPlayers.Children.Clear();
        for (int i = 0; i < view.Players.Count; i++)
            _worldPlayers.Children.Add(BuildPlayerRow(view, i, godNames, brushes[i]));

        BuildDiplomacyGrid(view, brushes);
    }

    // Campaign files often leave all explicit colors 0 (set by script); fall back
    // to the id-based roster the 3D preview uses so both stay consistent.
    // Reuses the cached brushes the entity player picker already builds.
    static IBrush[] BuildPlayerBrushes(ScenarioPlayersView view)
    {
        bool anyExplicit = false;
        foreach (var p in view.Players)
            if (p.Color != 0) { anyExplicit = true; break; }

        _playerOptions ??= BuildPlayerOptions();
        var brushes = new IBrush[view.Players.Count];
        for (int i = 0; i < brushes.Length; i++)
        {
            var colorIndex = anyExplicit ? (byte)System.Math.Min(view.Players[i].Color, 255) : (byte)i;
            brushes[i] = _playerOptions[System.Math.Min(colorIndex, (byte)(_playerOptions.Count - 1))].Brush;
        }
        return brushes;
    }

    // One grid per card so the label/field columns align across all rows:
    //   [swatch] [name...................]
    //   God      [combo.................]
    //   Age      [combo....] Pop  [.....]
    //   Gold     [.........] Wood [.....]
    //   Food     [.........] Favor [....]
    //   AI: path
    Control BuildPlayerRow(ScenarioPlayersView view, int index, List<string>? godNames, IBrush brush)
    {
        var pl = view.Players[index];

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("42,*,42,*") };
        int row = 0;

        void AddRowDef() => grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        void Place(Control c, int r, int col, int span = 1)
        {
            var m = c.Margin;
            double top = r > 0 ? System.Math.Max(m.Top, 4) : m.Top;
            double left = col > 0 ? System.Math.Max(m.Left, c is TextBlock ? 8 : 4) : m.Left;
            c.Margin = new Avalonia.Thickness(left, top, m.Right, m.Bottom);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, col);
            Grid.SetColumnSpan(c, span);
            grid.Children.Add(c);
        }

        // Name row
        AddRowDef();
        var swatch = new Border
        {
            Width = 14, Height = 14,
            Background = brush,
            CornerRadius = new Avalonia.CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(swatch, $"Player {index} color");
        var nameBox = new TextBox
        {
            Text = pl.Name,
            PlaceholderText = index == 0 ? "Player 0 (Nature)" : $"Player {index}",
            FontSize = 12,
            Padding = new Avalonia.Thickness(6, 3),
            MinHeight = 0,
            Height = ControlHeight,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        nameBox.LostFocus += (_, _) => CommitName(pl, nameBox);
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { CommitName(pl, nameBox); e.Handled = true; }
        };
        Place(swatch, row, 0);
        Place(nameBox, row, 1, span: 3);
        row++;

        // God row
        AddRowDef();
        Place(RowLabel("God"), row, 0);
        if (godNames is not null)
        {
            var combo = CompactCombo();
            // Index 0 = "(None)" (god 0 = no major god, common on Mother Nature);
            // real gods follow at their 1-based ids.
            var items = new List<string>(godNames.Count + 2) { "(None)" };
            items.AddRange(godNames);
            int selected = (int)pl.God;
            if (selected < 0 || selected >= items.Count)
            {
                items.Add($"(unknown {pl.God})");
                selected = items.Count - 1;
            }
            combo.ItemsSource = items;
            combo.SelectedIndex = selected;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0 || combo.SelectedIndex > godNames.Count) return;
                CommitPlayer(pl, f => f with { God = (uint)combo.SelectedIndex });
            };
            Place(combo, row, 1, span: 3);
        }
        else
        {
            var godText = new TextBlock
            {
                Text = pl.God.ToString(),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#dddddd")),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Place(godText, row, 1, span: 3);
        }
        row++;

        // Age + pop row
        AddRowDef();
        Place(RowLabel("Age"), row, 0);
        var ageCombo = CompactCombo();
        var ageItems = new List<string> { "(unset)", "Archaic", "Classical", "Heroic", "Mythic" };
        int ageSel = pl.StartAge == uint.MaxValue ? 0 : (int)pl.StartAge + 1;
        if (ageSel < 0 || ageSel >= ageItems.Count) { ageItems.Add($"({pl.StartAge})"); ageSel = ageItems.Count - 1; }
        ageCombo.ItemsSource = ageItems;
        ageCombo.SelectedIndex = ageSel;
        ageCombo.SelectionChanged += (_, _) =>
        {
            if (ageCombo.SelectedIndex is < 0 or > 4) return;
            var newAge = ageCombo.SelectedIndex == 0 ? uint.MaxValue : (uint)(ageCombo.SelectedIndex - 1);
            CommitPlayer(pl, f => f with { StartAge = newAge });
        };
        Place(ageCombo, row, 1);
        Place(RowLabel("Pop"), row, 2);
        var popNum = SmallNumeric(pl.PopLimit, -1, 100000);
        popNum.ValueChanged += (_, _) =>
        {
            if (popNum.Value is { } v)
                CommitPlayer(pl, f => f with { PopLimit = (int)v });
        };
        Place(popNum, row, 3);
        row++;

        // Resources: 2x2 with full-word labels
        AddRowDef();
        Place(RowLabel("Gold"), row, 0);
        Place(ResourceNumeric(pl.Gold, (f, v) => f with { Gold = v }, pl), row, 1);
        Place(RowLabel("Wood"), row, 2);
        Place(ResourceNumeric(pl.Wood, (f, v) => f with { Wood = v }, pl), row, 3);
        row++;

        AddRowDef();
        Place(RowLabel("Food"), row, 0);
        Place(ResourceNumeric(pl.Food, (f, v) => f with { Food = v }, pl), row, 1);
        Place(RowLabel("Favor"), row, 2);
        Place(ResourceNumeric(pl.Favor, (f, v) => f with { Favor = v }, pl), row, 3);
        row++;

        if (!string.IsNullOrEmpty(pl.AiPath))
        {
            AddRowDef();
            var ai = new TextBlock
            {
                Text = $"AI: {pl.AiPath}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#7e8aa0")),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(ai, pl.AiPath);
            Place(ai, row, 0, span: 4);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1b212b")),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(8, 6),
            Child = grid,
        };
    }

    NumericUpDown ResourceNumeric(float value, System.Func<PlayerFields, float, PlayerFields> mutate, ScenarioPlayer pl)
    {
        var num = SmallNumeric((decimal)value, 0, 10000000);
        num.ValueChanged += (_, _) =>
        {
            if (num.Value is not { } v) return;
            CommitPlayer(pl, f =>
            {
                f = mutate(f, (float)v);
                return f with { ResTotal = f.Gold + f.Wood + f.Food + f.Favor };
            });
        };
        return num;
    }

    static ComboBox CompactCombo() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        FontSize = 12,
        Padding = new Avalonia.Thickness(6, 3),
        MinHeight = 0,
        Height = ControlHeight,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    void CommitPlayer(ScenarioPlayer pl, System.Func<PlayerFields, PlayerFields> mutate)
        => ExecuteWorldCommand(SetPlayerFields.Create(pl, mutate(PlayerFields.From(pl))));

    void CommitName(ScenarioPlayer pl, TextBox box)
        => CommitPlayer(pl, f => f with { Name = box.Text ?? "" });

    static TextBlock RowLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.Parse("#9aa5b5")),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Match ComboBox height; TextBox/NumericUpDown default MinHeight is taller
    // and makes rows waste vertical space.
    const double ControlHeight = 26;

    // Height comes from the inner template TextBox (slimmed via the scoped style
    // on _worldPlayers); clamping the host Height instead clips the box borders.
    static NumericUpDown SmallNumeric(decimal value, decimal min, decimal max) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        Increment = 1,
        ShowButtonSpinner = false,
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        MinWidth = 0,
        MinHeight = 0,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    void BuildDiplomacyGrid(ScenarioPlayersView view, IBrush[] brushes)
    {
        _diplomacyGrid.Children.Clear();
        _diplomacyGrid.RowDefinitions.Clear();
        _diplomacyGrid.ColumnDefinitions.Clear();

        int n = view.Players.Count;
        const double cell = 22;

        for (int i = 0; i <= n; i++)
        {
            _diplomacyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _diplomacyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        for (int i = 0; i < n; i++)
        {
            var colHdr = HeaderSwatch(view, i, brushes[i]);
            Grid.SetRow(colHdr, 0);
            Grid.SetColumn(colHdr, i + 1);
            _diplomacyGrid.Children.Add(colHdr);

            var rowHdr = HeaderSwatch(view, i, brushes[i]);
            Grid.SetRow(rowHdr, i + 1);
            Grid.SetColumn(rowHdr, 0);
            _diplomacyGrid.Children.Add(rowHdr);
        }

        for (int row = 0; row < n; row++)
        {
            var pl = view.Players[row];
            for (int col = 0; col < n; col++)
            {
                Control c;
                if (row == col || col >= pl.Diplomacy.Count)
                {
                    c = new Border
                    {
                        Width = cell, Height = cell,
                        Background = StanceSelf,
                        Margin = new Avalonia.Thickness(1),
                        CornerRadius = new Avalonia.CornerRadius(2),
                    };
                }
                else
                {
                    var stance = pl.Diplomacy[col];
                    var btn = new Button
                    {
                        Width = cell, Height = cell,
                        Margin = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(0),
                        Background = StanceBrush(stance),
                        BorderThickness = new Avalonia.Thickness(0),
                        CornerRadius = new Avalonia.CornerRadius(2),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    if (stance is < 1 or > 3)
                    {
                        btn.Content = new TextBlock
                        {
                            Text = stance.ToString(), FontSize = 10,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                    }
                    ToolTip.SetTip(btn, $"P{row} -> P{col}: {StanceName(stance)}");
                    int slot = col;
                    btn.Click += (_, _) =>
                    {
                        var current = pl.Diplomacy[slot];
                        var next = current is >= 1 and < 3 ? current + 1 : 1;
                        ExecuteWorldCommand(SetPlayerDiplomacy.Create(pl, slot, next));
                        BuildDiplomacyGrid(view, brushes);
                    };
                    c = btn;
                }
                Grid.SetRow(c, row + 1);
                Grid.SetColumn(c, col + 1);
                _diplomacyGrid.Children.Add(c);
            }
        }
    }

    static Control HeaderSwatch(ScenarioPlayersView view, int index, IBrush brush)
    {
        var rect = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 14, Height = 14,
            Fill = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = string.IsNullOrEmpty(view.Players[index].Name) ? $"Player {index}" : view.Players[index].Name;
        ToolTip.SetTip(rect, label);
        return new Border { Width = 24, Height = 24, Child = rect };
    }

    static IBrush StanceBrush(int stance) => stance switch
    {
        1 => StanceAlly,
        3 => StanceEnemy,
        _ => StanceNeutral,
    };

    static string StanceName(int stance) => stance switch
    {
        0 => "self",
        1 => "ally",
        2 => "neutral",
        3 => "enemy",
        _ => stance.ToString(),
    };
}
