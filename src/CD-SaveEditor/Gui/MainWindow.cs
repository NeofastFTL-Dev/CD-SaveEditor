using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CDSaveEditor.Game;

namespace CDSaveEditor.Gui;

/// <summary>The editor window: the same operations the CLI offers, without a browser.</summary>
public sealed class MainWindow : Window
{
    private static readonly IBrush Page = Solid("#14161a");
    private static readonly IBrush CardFill = Solid("#1b1e24");
    private static readonly IBrush Line = Solid("#262a31");
    private static readonly IBrush Muted = Solid("#8b939e");
    private static readonly IBrush Accent = Solid("#2f7d4f");
    private static readonly IBrush Selected = Solid("#2f3540");

    private readonly EditorSession _session;
    private readonly StackPanel _slotButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 13 };
    private readonly TextBlock _game = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 13 };
    private readonly WrapPanel _combo = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _flags = new() { Spacing = 2 };
    private readonly TextBox _psc = new() { Watermark = "profile .psc path", MinWidth = 140 };
    private readonly TextBlock _status = new() { Name = "StatusText", TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 13 };
    private readonly TextBlock _imageLabel = new() { Foreground = Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };

    private readonly Button _unlock;
    private readonly Button _restore;
    private readonly Button _export;
    private readonly Button _import;
    private readonly Button _apply;

    private UiState? _current;
    private int _active;

    public MainWindow(string? image = null, string? iso = null)
    {
        _session = new EditorSession(image, iso);
        Title = "CD-SaveEditor";
        Width = 920;
        Height = 780;
        MinWidth = 720;
        MinHeight = 560;
        Background = Page;

        _unlock = Primary("Unlock everything");
        _restore = Plain("Restore original");
        _export = Plain("Export profiles");
        _import = Plain("Write to xemu");
        _apply = Primary("Apply combination");
        var browse = Plain("Browse…");

        _unlock.Click += async (_, _) => await ActAsync("writing…", _session.Unlock);
        _restore.Click += async (_, _) => await ActAsync("restoring…", _session.Restore);
        _export.Click += async (_, _) => await ActAsync("exporting…", _session.Export);
        _import.Click += async (_, _) => await ActAsync("writing…", () => _session.Import(_psc.Text ?? "", _active));
        _apply.Click += async (_, _) => await ActAsync("writing…", () => _session.Apply(SelectedFlags()));
        browse.Click += async (_, _) => await PickProfileAsync();

        // Two columns: the actions on the left, the read-only game state and the profile
        // list on the right, which takes whatever height is left over. Everything stays
        // above the fold at the default window size, and the outer ScrollViewer keeps it
        // reachable when the window is shrunk.
        var left = new StackPanel { Spacing = 12 };
        left.Children.Add(Card("Save in the HDD",
            _slotButtons,
            _state,
            Row(_unlock, _restore),
            Hint("A timestamped copy of the HDD image is kept next to it before writing.")));
        left.Children.Add(Card("Export / import",
            PathRow(_psc, browse),
            Row(_export, _import),
            Hint("Writes the profile straight into the xemu HDD image at the selected slot, "
                 + "so a save travels between machines without booting the game.")));
        left.Children.Add(Card("Lock flags",
            _combo,
            Row(_apply),
            Hint("Checked = unlocked (0). Unchecked = the game's own lock mask.")));

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 12 };
        right.Children.Add(Card("Game files",
            _game,
            Hint("The game rebuilds its profile from these defaults and rejects edited saves, "
                 + "so unlocking patches the startup config too. Start the game once afterwards.")));
        var profile = FillingCard("Profile", new ScrollViewer { Content = _flags, Padding = new Thickness(0, 0, 6, 0) });
        Grid.SetRow(profile, 1);
        right.Children.Add(profile);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 14 };
        body.Children.Add(Header());
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14 };
        columns.Children.Add(left);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        Grid.SetRow(columns, 1);
        body.Children.Add(columns);

        var content = new Border { Margin = new Thickness(24, 18, 24, 18), Child = body };

        var statusBar = new Border
        {
            Background = CardFill,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 10),
            Child = _status,
        };
        var root = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);
        root.Children.Add(new ScrollViewer { Content = content });
        Content = root;

        Opened += async (_, _) =>
        {
            await ActAsync("reading the HDD image…", () => _session.Load());
            Ready?.Invoke();
        };
    }

    /// <summary>Raised once the first load has been shown (used by the screenshot helper).</summary>
    public event Action? Ready;

    /// <summary>Number of save slots currently listed (used by the UI smoke test).</summary>
    public int SlotCount => _slotButtons.Children.Count;

    public string StatusText => _status.Text ?? string.Empty;

    private async Task ActAsync(string label, Func<UiState> action)
    {
        SetBusy(true, label);
        try
        {
            Apply(await Task.Run(action));
        }
        catch (Exception exception) when (exception is IOException or ToolException
                                              or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = "failed: " + exception.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? label)
    {
        foreach (var button in new[] { _unlock, _restore, _export, _import, _apply })
        {
            button.IsEnabled = !busy;
        }
        if (label is not null)
        {
            _status.Text = label;
        }
    }

    private void Apply(UiState state)
    {
        _current = state;
        _imageLabel.Text = state.Image ?? "no HDD image found";
        if (state.Error is { } error)
        {
            _status.Text = "failed: " + error;
        }
        else if (state.Message.Length > 0)
        {
            _status.Text = state.Message;
        }
        else
        {
            _status.Text = state.Slots.Count > 0
                ? $"Read {state.Slots.Count} save(s) from the HDD image."
                : "No Cel Damage save found in this image.";
        }

        _slotButtons.Children.Clear();
        _flags.Children.Clear();
        _combo.Children.Clear();

        if (state.Slots.Count == 0)
        {
            _state.Text = "No Cel Damage save found in this image.";
        }
        else
        {
            _active = Math.Clamp(_active, 0, state.Slots.Count - 1);
            for (var i = 0; i < state.Slots.Count; i++)
            {
                var index = i;
                var button = new Button
                {
                    Content = $"slot {i + 1}",
                    Padding = new Thickness(12, 6),
                    FontSize = 13,
                    Background = i == _active ? Selected : CardFill,
                };
                button.Click += (_, _) =>
                {
                    _active = index;
                    if (_current is not null)
                    {
                        Apply(_current);
                    }
                };
                _slotButtons.Children.Add(button);
            }

            var slot = state.Slots[_active];
            _state.Text = slot.Unlocked
                ? "Everything is already unlocked."
                : "Locked content present.";
            BuildFlags(slot);
            BuildCombo(slot);
        }

        _game.Text = state.Game.Found
            ? $"{state.Game.Path}  —  startup config: "
              + (state.Game.Patched ? "patched, new profiles start unlocked" : "original (locked defaults)")
            : "No Cel Damage ISO found (pass --iso).";
    }

    private void BuildFlags(SlotInfo slot)
    {
        foreach (var (key, value) in slot.Fields)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = key, Foreground = Muted, FontSize = 13 });
            var number = new TextBlock
            {
                Text = value.ToString(),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(number, 1);
            row.Children.Add(number);
            _flags.Children.Add(row);
        }
    }

    private void BuildCombo(SlotInfo slot)
    {
        var current = slot.Fields.ToDictionary(field => field.Key, field => field.Value);
        foreach (var key in GameFiles.Keys)
        {
            var open = current.TryGetValue(key, out var value) && value == 0;
            _combo.Children.Add(new CheckBox
            {
                Content = key.Replace("LockFlags", "", StringComparison.Ordinal),
                IsChecked = open,
                Tag = key,
                FontSize = 13,
                Margin = new Thickness(0, 0, 14, 2),
            });
        }
    }

    private IReadOnlyDictionary<string, bool> SelectedFlags() =>
        _combo.Children.OfType<CheckBox>()
            .Where(box => box.Tag is string)
            .ToDictionary(box => (string)box.Tag!, box => box.IsChecked == true);

    private async Task PickProfileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Cel Damage profile",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Cel Damage profile") { Patterns = new[] { "*.psc" } },
            },
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _psc.Text = path;
        }
    }

    private Control Header()
    {
        _imageLabel.Text = "looking for the HDD image…";
        var title = new TextBlock { Text = "CD-SaveEditor", FontSize = 22, FontWeight = FontWeight.SemiBold };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(title);
        stack.Children.Add(_imageLabel);
        return stack;
    }

    private static Control Card(string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 9 };
        stack.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Foreground = Muted,
            FontSize = 12,
            LetterSpacing = 1,
        });
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }
        return new Border
        {
            Background = CardFill,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 14),
            Child = stack,
        };
    }

    private static Control Row(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }
        return panel;
    }

    /// <summary>A row with a control that takes the free width and one that keeps its own.</summary>
    private static Control PathRow(Control stretch, Control fixedWidth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(stretch);
        Grid.SetColumn(fixedWidth, 1);
        grid.Children.Add(fixedWidth);
        return grid;
    }

    /// <summary>A card whose single child is stretched to fill the card's height.</summary>
    private static Control FillingCard(string title, Control content)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 9 };
        grid.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Foreground = Muted,
            FontSize = 12,
            LetterSpacing = 1,
        });
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        return new Border
        {
            Background = CardFill,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 14),
            Child = grid,
        };
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = Muted,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button Primary(string text) => new()
    {
        Content = text,
        Background = Accent,
        Foreground = Brushes.White,
        Padding = new Thickness(16, 9),
    };

    private static Button Plain(string text) => new()
    {
        Content = text,
        Padding = new Thickness(16, 9),
    };

    private static IBrush Solid(string hex) => new SolidColorBrush(Color.Parse(hex));
}
