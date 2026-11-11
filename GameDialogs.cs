using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using static SaveDock.L;

namespace SaveDock;

internal abstract class GameDialog : Window
{
    protected GameDialog(string title, int height)
    {
        Title = title;
        Width = 610;
        Height = height;
        MinWidth = 480;
        MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#181D29");
        Foreground = Brush.Parse("#EDF1FA");
        FontFamily = new FontFamily("fonts:Inter#Inter");
    }

    protected static TextBlock Text(string text, bool heading = false) => new()
    {
        Text = text,
        FontSize = heading ? 20 : 13,
        FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal,
        Foreground = Brush.Parse(heading ? "#EDF1FA" : "#A5B0C4"),
        TextWrapping = TextWrapping.Wrap
    };

    protected static Button Action(string title, bool primary = false) => new()
    {
        Content = title,
        Padding = new Thickness(12, 8),
        MinHeight = 36,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = Brush.Parse(primary ? "#85E8CA" : "#252D3E"),
        Foreground = Brush.Parse(primary ? "#10251E" : "#EDF1FA"),
        CornerRadius = new CornerRadius(7)
    };

    protected static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls)
            row.Children.Add(control);
        return row;
    }

    protected static void Add(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }
}

internal sealed class RestoreDialog : GameDialog
{
    private readonly BackupStore store;
    private readonly ComboBox versions;
    private readonly Button restore;
    private readonly ListBox targets;
    private readonly TextBlock details;
    private int previewRequest;

    public RestoreDialog(string game, BackupVersion[] available, BackupStore store)
        : base(T("Restore game saves", "Spielstand laden"), 465)
    {
        this.store = store;
        var grid = new Grid { Margin = new Thickness(22), RowSpacing = 10, RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto,Auto") };
        Add(grid, Text(game, heading: true), 0);
        var versionLabel = T("Backup version", "Sicherungsversion");
        Add(grid, Text(versionLabel), 1);
        versions = new ComboBox { ItemsSource = available, HorizontalAlignment = HorizontalAlignment.Stretch };
        versions.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, versionLabel);
        Add(grid, versions, 2);
        details = Text("");
        Add(grid, details, 3);
        targets = new ListBox { Background = Brush.Parse("#121723") };
        ScrollViewer.SetHorizontalScrollBarVisibility(targets, ScrollBarVisibility.Auto);
        Add(grid, targets, 4);
        Add(grid, Text(T("Close the game before restoring. Existing files and registry values may be overwritten.",
            "Schließe das Spiel vor dem Laden. Vorhandene Dateien und Registry-Werte können überschrieben werden.")), 5);

        var cancel = Action(T("Cancel", "Abbrechen"));
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(null);
        restore = Action(T("Restore game saves", "Spielstand laden"), primary: true);
        restore.IsEnabled = false;
        restore.Click += (_, _) => Close(versions.SelectedItem as BackupVersion);
        var buttons = Row(cancel, restore);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Add(grid, buttons, 6);
        Content = grid;

        versions.SelectionChanged += async (_, _) => await PreviewAsync();
        versions.SelectedIndex = 0;
        Closed += (_, _) => previewRequest++;
    }

    private async Task PreviewAsync()
    {
        var request = ++previewRequest;
        restore.IsEnabled = false;
        targets.ItemsSource = null;
        if (versions.SelectedItem is not BackupVersion selected)
            return;

        details.Text = T("Checking destination paths…", "Zielpfade werden ermittelt …");
        try
        {
            var plan = await Task.Run(() => store.Preview(selected));
            if (request != previewRequest)
                return;
            var registry = plan.Registry is null ? "" : T(" · Includes registry data", " · Registry-Daten enthalten");
            var fileLabel = plan.Files.Length == 1 ? T("file", "Datei") : T("files", "Dateien");
            details.Text = $"{plan.Files.Length} {fileLabel}{registry}";
            targets.ItemsSource = plan.Files.Select(file => file.Target).ToArray();
            restore.IsEnabled = plan.Files.Length > 0 || plan.Registry is not null;
        }
        catch (Exception error)
        {
            if (request == previewRequest)
                details.Text = error.Message;
        }
    }
}

internal sealed class UndoDialog : GameDialog
{
    public UndoDialog(UndoSnapshot snapshot) : base(T("Undo restore", "Laden rückgängig machen"), 440)
    {
        var grid = new Grid { Margin = new Thickness(22), RowSpacing = 10, RowDefinitions = new("Auto,Auto,*,Auto,Auto") };
        Add(grid, Text(snapshot.Game, heading: true), 0);
        var registry = snapshot.Registry.Values.Length > 0 || snapshot.Registry.CreatedKeys.Length > 0
            ? T(" · Registry entries will be reverted", " · Registry-Einträge werden zurückgesetzt") : "";
        var when = snapshot.Created.ToLocalTime().ToString("g", L.Culture);
        var loaded = snapshot.LoadedVersion.ToLocalTime().ToString("g", L.Culture);
        Add(grid, Text(T($"State before the restore on {when}\nBackup restored: {loaded}",
            $"Zustand vor dem Laden am {when}\nGeladene Sicherung vom {loaded}") + registry), 1);

        var paths = new ListBox
        {
            Background = Brush.Parse("#121723"),
            ItemsSource = snapshot.Files.Select(file =>
                $"{(file.Entry is null ? T("Remove", "Entfernen") : T("Restore previous file", "Zurücksetzen"))}  ·  {file.Target}").ToArray(),
            IsVisible = snapshot.Files.Length > 0
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(paths, ScrollBarVisibility.Auto);
        Add(grid, paths, 2);
        Add(grid, Text(T("Close the game. Changes made since the restore may be overwritten.",
            "Schließe das Spiel. Änderungen seit dem Laden können überschrieben werden.")), 3);

        var cancel = Action(T("Cancel", "Abbrechen"));
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(false);
        var undo = Action(T("Undo restore", "Laden rückgängig machen"), primary: true);
        undo.Click += (_, _) => Close(true);
        var buttons = Row(cancel, undo);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Add(grid, buttons, 4);
        Content = grid;
    }
}

internal sealed record CustomGameEdit(CustomGame? Game);

internal sealed class CustomGameDialog : GameDialog
{
    private readonly CustomGame? original;
    private readonly HashSet<string> names;
    private readonly List<string> paths;
    private readonly TextBox name;
    private readonly ListBox sourceList;
    private readonly TextBlock iconText;
    private readonly TextBlock feedback;
    private string? iconPath;

    public CustomGameDialog(CustomGame? game, IEnumerable<string> names, string? knownName = null)
        : base(game is null && knownName is null ? T("Add game", "Spiel hinzufügen") :
            T("Custom save paths", "Eigene Spielstandpfade"), 500)
    {
        original = game;
        this.names = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths = (game?.Paths ?? []).ToList();
        iconPath = game?.IconPath;

        var grid = new Grid { Margin = new Thickness(22), RowSpacing = 9,
            RowDefinitions = new("Auto,Auto,Auto,*,Auto,Auto,Auto") };
        Add(grid, Text(game is null && knownName is null ? T("Add a custom game", "Eigenes Spiel hinzufügen") :
            T("Custom save paths", "Eigene Speicherpfade"), heading: true), 0);
        var nameLabel = T("Game name", "Spielname");
        name = new TextBox { Text = game?.Name ?? knownName, PlaceholderText = nameLabel, MaxLength = 110,
            IsReadOnly = game is not null || knownName is not null };
        name.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, nameLabel);
        Add(grid, name, 1);

        var folder = Action(T("Add folder", "Ordner hinzufügen"));
        folder.Click += async (_, _) => await AddFoldersAsync();
        var files = Action(T("Add files", "Dateien hinzufügen"));
        files.Click += async (_, _) => await AddFilesAsync();
        var remove = Action(T("Remove path", "Pfad entfernen"));
        remove.IsEnabled = false;
        remove.Click += (_, _) =>
        {
            if (sourceList!.SelectedItem is string path)
            {
                paths.Remove(path);
                UpdatePaths();
            }
        };
        Add(grid, Row(folder, files, remove), 2);

        sourceList = new ListBox { Background = Brush.Parse("#121723") };
        sourceList.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, T("Save paths", "Spielstandpfade"));
        sourceList.SelectionChanged += (_, _) => remove.IsEnabled = sourceList.SelectedItem is not null;
        ScrollViewer.SetHorizontalScrollBarVisibility(sourceList, ScrollBarVisibility.Auto);
        Add(grid, sourceList, 3);

        var icon = Action(T("Choose icon", "Icon auswählen"));
        icon.Click += async (_, _) => await ChooseIconAsync();
        var clearIcon = Action(T("Remove icon", "Icon entfernen"));
        clearIcon.Click += (_, _) => { iconPath = null; UpdateIcon(); };
        iconText = Text("");
        iconText.VerticalAlignment = VerticalAlignment.Center;
        iconText.TextTrimming = TextTrimming.CharacterEllipsis;
        iconText.MaxWidth = 200;
        Add(grid, Row(icon, clearIcon, iconText), 4);

        feedback = Text("");
        Add(grid, feedback, 5);
        var cancel = Action(T("Cancel", "Abbrechen"));
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(null);
        var save = Action(T("Save", "Speichern"), primary: true);
        save.Click += (_, _) => Save();
        var buttons = Row(cancel, save);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        if (original is not null)
        {
            var delete = Action(T("Remove entry", "Eintrag entfernen"));
            delete.Click += (_, _) => Close(new CustomGameEdit(null));
            buttons.Children.Insert(0, delete);
        }
        Add(grid, buttons, 6);
        Content = grid;
        UpdatePaths();
        UpdateIcon();
    }

    private async Task AddFoldersAsync()
    {
        try
        {
            var result = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = T("Choose save folders", "Spielstandordner auswählen"), AllowMultiple = true });
            AddPaths(result);
        }
        catch (Exception error) { feedback.Text = error.Message; }
    }

    private async Task AddFilesAsync()
    {
        try
        {
            var result = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { Title = T("Choose save files", "Spielstanddateien auswählen"), AllowMultiple = true });
            AddPaths(result);
        }
        catch (Exception error) { feedback.Text = error.Message; }
    }

    private void AddPaths(IEnumerable<IStorageItem> items)
    {
        foreach (var item in items)
            if (item.TryGetLocalPath() is { } path && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                paths.Add(path);
        UpdatePaths();
    }

    private async Task ChooseIconAsync()
    {
        try
        {
            var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Choose a game executable or image", "Spiel-EXE oder Bild auswählen"),
                FileTypeFilter = [new FilePickerFileType(T("Game executable or image", "Spiel-EXE oder Bild"))
                    { Patterns = ["*.exe", "*.ico", "*.png", "*.jpg", "*.jpeg"] }]
            });
            if (result.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                iconPath = path;
                UpdateIcon();
            }
        }
        catch (Exception error) { feedback.Text = error.Message; }
    }

    private void UpdatePaths() => sourceList.ItemsSource = paths.ToArray();
    private void UpdateIcon() => iconText.Text = iconPath is null ? "Optional" : Path.GetFileName(iconPath);

    private void Save()
    {
        var title = name.Text?.Trim();
        if (string.IsNullOrEmpty(title) || paths.Count == 0)
        {
            feedback.Text = T("Enter a game name and at least one save path.", "Gib einen Spielnamen und mindestens einen Spielstandpfad an.");
            return;
        }
        if (original is null && names.Contains(title))
        {
            feedback.Text = T("A custom entry already exists for this game.", "Für diesen Spielnamen gibt es bereits einen eigenen Eintrag.");
            return;
        }
        Close(new CustomGameEdit(new CustomGame(title, paths.ToArray(), iconPath)));
    }
}
