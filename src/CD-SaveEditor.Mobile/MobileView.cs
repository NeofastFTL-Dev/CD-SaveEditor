using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CDSaveEditor.Game;
using CDSaveEditor.Saves;

namespace CDSaveEditor.Mobile;

/// <summary>
/// The phone/tablet screen: pick the HDD image, unlock or apply lock flags, and write
/// the edited copy back over the file the user picked.
/// </summary>
public sealed class MobileView : UserControl
{
    private static readonly IBrush Page = Solid("#14161a");
    private static readonly IBrush CardFill = Solid("#1b1e24");
    private static readonly IBrush Line = Solid("#262a31");
    private static readonly IBrush Muted = Solid("#8b939e");
    private static readonly IBrush Accent = Solid("#2f7d4f");
    private static readonly IBrush Selected = Solid("#2f3540");

    private readonly TextBlock _imageText = new() { Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _isoText = new() { Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _gameText = new() { Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _slots = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _state = new() { Foreground = Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _combo = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _flags = new() { Spacing = 3 };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap };

    private readonly Button _choose;
    private readonly Button _saveBack;
    private readonly Button _saveCopy;
    private readonly Button _unlock;
    private readonly Button _restore;
    private readonly Button _apply;
    private readonly Button _isoChoose;
    private readonly Button _isoSave;
    private readonly Button _import;
    private readonly Button _export;

    private ManagedFile? _image;
    private ManagedFile? _iso;
    private UiState? _current;
    private int _active;
    private bool _busy;

    /// <param name="image">An image already on disk (used by the desktop preview).</param>
    /// <param name="iso">A game ISO already on disk (used by the desktop preview).</param>
    public MobileView(string? image = null, string? iso = null)
    {
        Background = Page;

        _choose = Primary("Choose HDD image…");
        _saveBack = Plain("Save back");
        _saveCopy = Plain("Save a copy…");
        _unlock = Primary("Unlock everything");
        _restore = Plain("Restore original");
        _apply = Primary("Apply combination");
        _isoChoose = Plain("Choose game ISO…");
        _isoSave = Plain("Save ISO back");
        _import = Plain("Import .psc…");
        _export = Plain("Export profiles…");

        _choose.Click += async (_, _) => await ChooseImageAsync();
        _saveBack.Click += async (_, _) => await SaveBackAsync();
        _saveCopy.Click += async (_, _) => await SaveCopyAsync(saveBack: false);
        _unlock.Click += async (_, _) => await ActAsync("unlocking…", () => Session.Unlock());
        _restore.Click += async (_, _) => await ActAsync("restoring…", () => Session.Restore());
        _apply.Click += async (_, _) => await ActAsync("writing…", () => Session.Apply(SelectedFlags()));
        _isoChoose.Click += async (_, _) => await ChooseIsoAsync();
        _isoSave.Click += async (_, _) => await SaveIsoAsync();
        _import.Click += async (_, _) => await ImportAsync();
        _export.Click += async (_, _) => await ExportAsync();

        var content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(14, 14, 14, 16),
        };
        content.Children.Add(Header());
        content.Children.Add(Card("HDD image",
            _imageText,
            Actions(_choose),
            Actions(_saveBack, _saveCopy),
            Hint("The image is copied into the app and edited there. \"Save back\" replaces "
                 + "the file you picked; keep a copy on the PC if it matters.")));
        content.Children.Add(Card("Saves",
            _slots,
            _state,
            Actions(_unlock, _restore),
            Hint("Unlocking also patches the game ISO below so the game keeps the profile.")));
        content.Children.Add(Card("Lock flags",
            _combo,
            Actions(_apply),
            Hint("Checked = unlocked. Needs the game ISO: it supplies the game's lock masks.")));
        content.Children.Add(Card("Profile", _flags));
        content.Children.Add(Card("Game files",
            _gameText,
            _isoText,
            Actions(_isoChoose, _isoSave),
            Hint("Optional. Without it unlock still rewrites the save, but the game "
                 + "rebuilds the profile from its own defaults on the next boot.")));
        content.Children.Add(Card("Profiles",
            Actions(_import, _export),
            Hint("Import writes a .psc into the selected slot; export saves the "
                 + "profiles to a folder you choose.")));

        var statusBar = new Border
        {
            Background = CardFill,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10),
            Child = _status,
        };
        var root = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);
        root.Children.Add(new ScrollViewer { Content = content });
        Content = root;

        if (image is not null)
        {
            _image = ManagedFile.FromLocalPath(image);
        }
        if (iso is not null)
        {
            _iso = ManagedFile.FromLocalPath(iso);
        }

        UpdateButtons();
        // Load once the view has been laid out: filling the panels from inside the
        // attach pass leaves them measured with their empty heights.
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            async () =>
            {
                if (_image is not null)
                {
                    await LoadAsync();
                }
                Ready?.Invoke();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Raised once the first load has been shown (used by the screenshot helper).</summary>
    public event Action? Ready;

    private EditorSession Session => new(_image?.LocalPath, _iso?.LocalPath, backup: false);

    private Control Header()
    {
        var title = new TextBlock { Text = "CD-SaveEditor", FontSize = 22, FontWeight = FontWeight.SemiBold };
        var subtitle = new TextBlock
        {
            Text = "Cel Damage saves, straight from the HDD image",
            Foreground = Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(title);
        stack.Children.Add(subtitle);
        return stack;
    }

    private async Task ChooseImageAsync()
    {
        var file = await PickAsync("Choose the Xbox HDD image");
        if (file is null)
        {
            return;
        }
        var properties = await file.GetBasicPropertiesAsync();
        SetStatus($"copying {file.Name} ({ManagedFile.Describe((long)(properties.Size ?? 0))})…");
        try
        {
            _image = await ManagedFile.CopyInAsync(file, "hdd");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus("could not copy the image: " + exception.Message);
            return;
        }
        _active = 0;
        await LoadAsync();
    }

    private async Task ChooseIsoAsync()
    {
        var file = await PickAsync("Choose the Cel Damage ISO");
        if (file is null)
        {
            return;
        }
        var properties = await file.GetBasicPropertiesAsync();
        SetStatus($"copying {file.Name} ({ManagedFile.Describe((long)(properties.Size ?? 0))})…");
        try
        {
            _iso = await ManagedFile.CopyInAsync(file, "game");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus("could not copy the ISO: " + exception.Message);
            return;
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_image is null)
        {
            return;
        }
        await ActAsync("scanning the image for saves…", () => Session.Load());
    }

    private async Task SaveBackAsync()
    {
        if (_image is null)
        {
            return;
        }
        if (_image.Source is null)
        {
            await SaveCopyAsync(saveBack: true);
            return;
        }
        await WriteAsync($"writing the image back to {_image.Name}…", _image, () => _image.Source!, "image");
    }

    private Task SaveCopyAsync(bool saveBack)
    {
        if (_image is null)
        {
            return Task.CompletedTask;
        }
        return PickAndWriteAsync("Save the edited HDD image", _image, "image");
    }

    private Task SaveIsoAsync()
    {
        if (_iso is null)
        {
            return Task.CompletedTask;
        }
        return _iso.Source is { } source
            ? WriteAsync($"writing the ISO back to {_iso.Name}…", _iso, () => source, "ISO")
            : PickAndWriteAsync("Save the edited ISO", _iso, "ISO");
    }

    private async Task PickAndWriteAsync(string title, ManagedFile file, string label)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        var target = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = file.Name,
        });
        if (target is null)
        {
            return;
        }
        await WriteAsync($"saving the {label}…", file, () => target, label);
    }

    private async Task WriteAsync(string label, ManagedFile file, Func<IStorageFile> target, string what)
    {
        SetBusy(true, label);
        try
        {
            await file.WriteToAsync(target());
            file.Dirty = false;
            SetStatus($"wrote the edited {what} to {file.Name}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"could not write the {what} ({exception.Message}); try \"Save a copy…\".");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ImportAsync()
    {
        if (_image is null)
        {
            SetStatus("choose an HDD image first.");
            return;
        }
        var file = await PickAsync("Choose a Cel Damage profile", "*.psc");
        if (file is null)
        {
            return;
        }
        try
        {
            var local = await ManagedFile.CopyInAsync(file, "profile");
            await ActAsync("importing…", () => Session.Import(local.LocalPath, _active));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus("could not read the profile: " + exception.Message);
        }
    }

    private async Task ExportAsync()
    {
        if (_image is null || _current is null)
        {
            SetStatus("choose an HDD image first.");
            return;
        }
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should the profiles go?",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
        {
            return;
        }
        SetBusy(true, "exporting…");
        try
        {
            var state = await Task.Run(() => Session.Export());
            var written = 0;
            foreach (var path in Directory.GetFiles(AppPaths.SaveDir, "*.psc"))
            {
                var target = await folders[0].CreateFileAsync(Path.GetFileName(path));
                if (target is null)
                {
                    continue;
                }
                await using (var input = File.OpenRead(path))
                await using (var output = await target.OpenWriteAsync())
                {
                    await input.CopyToAsync(output);
                }
                written++;
            }
            Show(state);
            SetStatus($"exported {written} profile(s) to {folders[0].Name}.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<IStorageFile?> PickAsync(string title, params string[] patterns)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Files")
                {
                    Patterns = patterns.Length > 0 ? patterns : new[] { "*.qcow2", "*.img", "*.raw" },
                },
            },
        });
        return files.Count > 0 ? files[0] : null;
    }

    private async Task ActAsync(string label, Func<UiState> action)
    {
        SetBusy(true, label);
        try
        {
            var state = await Task.Run(action);
            Show(state);
        }
        catch (Exception exception) when (exception is IOException or ToolException
                                              or UnauthorizedAccessException or InvalidDataException
                                              or FormatException)
        {
            SetStatus("failed: " + exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Show(UiState state)
    {
        _current = state;
        _imageText.Text = _image is null
            ? "No image chosen."
            : $"{_image.Name}  ·  {ManagedFile.Describe(_image.Length)}"
              + (_image.Dirty ? "  ·  edited" : "");
        _isoText.Text = _iso is null
            ? "No game ISO chosen."
            : $"{_iso.Name}  ·  {ManagedFile.Describe(_iso.Length)}";
        _gameText.Text = state.Game.Found
            ? "startup config: " + (state.Game.Patched
                ? "patched, new profiles start unlocked"
                : "original (locked defaults)")
            : "No startup config read yet.";

        if (state.Error is { } error)
        {
            SetStatus("failed: " + error);
        }
        else if (state.Message.Length > 0)
        {
            SetStatus(state.Message);
        }
        else
        {
            SetStatus(state.Slots.Count > 0
                ? $"Read {state.Slots.Count} save(s) from the image."
                : "No Cel Damage save found in this image.");
        }

        _slots.Children.Clear();
        _flags.Children.Clear();
        _combo.Children.Clear();

        if (state.Slots.Count == 0)
        {
            _state.Text = "Pick the HDD image that holds the Cel Damage profile.";
            UpdateButtons();
            return;
        }

        _active = Math.Clamp(_active, 0, state.Slots.Count - 1);
        for (var i = 0; i < state.Slots.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = $"slot {i + 1}",
                Padding = new Thickness(16, 10),
                FontSize = 14,
                Margin = new Thickness(0, 0, 8, 8),
                Background = i == _active ? Selected : CardFill,
            };
            button.Click += (_, _) =>
            {
                _active = index;
                if (_current is not null)
                {
                    Show(_current);
                }
            };
            _slots.Children.Add(button);
        }

        var slot = state.Slots[_active];
        _state.Text = slot.Unlocked ? "Everything is already unlocked." : "Locked content present.";
        BuildFlags(slot);
        BuildCombo(slot);
        UpdateButtons();
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
                FontSize = 14,
                Margin = new Thickness(0, 0, 14, 6),
            });
        }
    }

    private IReadOnlyDictionary<string, bool> SelectedFlags() =>
        _combo.Children.OfType<CheckBox>()
            .Where(box => box.Tag is string)
            .ToDictionary(box => (string)box.Tag!, box => box.IsChecked == true);

    private void UpdateButtons()
    {
        var hasImage = _image is not null && !_busy;
        var hasSaves = _current is { Slots.Count: > 0 } && !_busy;
        _choose.IsEnabled = !_busy;
        _saveBack.IsEnabled = hasImage && (_image?.Dirty ?? false);
        _saveCopy.IsEnabled = hasImage;
        _unlock.IsEnabled = hasSaves;
        _restore.IsEnabled = hasSaves;
        _apply.IsEnabled = hasSaves && _iso is not null;
        _isoChoose.IsEnabled = !_busy;
        _isoSave.IsEnabled = hasImage && _iso is not null;
        _import.IsEnabled = hasSaves;
        _export.IsEnabled = hasSaves;
    }

    private void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        if (label is not null)
        {
            SetStatus(label);
        }
        UpdateButtons();
    }

    private void SetStatus(string text) => _status.Text = text;

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
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14),
            Child = stack,
        };
    }

    /// <summary>Buttons that wrap onto a second line on a narrow screen.</summary>
    private static Control Actions(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls)
        {
            control.Margin = new Thickness(0, 0, 8, 4);
            panel.Children.Add(control);
        }
        return panel;
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
        Padding = new Thickness(18, 12),
        FontSize = 14,
    };

    private static Button Plain(string text) => new()
    {
        Content = text,
        Padding = new Thickness(18, 12),
        FontSize = 14,
    };

    private static IBrush Solid(string hex) => new SolidColorBrush(Color.Parse(hex));
}
