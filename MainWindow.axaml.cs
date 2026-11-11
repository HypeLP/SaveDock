using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using static SaveDock.L;

namespace SaveDock;

public partial class MainWindow : Window
{
    private readonly string dataDirectory;
    private readonly GameScanner scanner;
    private readonly UndoStore undoStore;
    private readonly CancellationTokenSource windowClosed = new();
    private readonly Dictionary<string, Bitmap?> icons = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GameSave> games = [];
    private BackupHistory history = BackupHistory.Empty;
    private HashSet<string> undoGames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> hiddenGames = new(StringComparer.OrdinalIgnoreCase);
    private UserSettings settings = new(Destination: null);
    private bool initialized;
    private bool busy;
    private bool writing;
    private bool updatingMode;

    public MainWindow() : this(
        Path.Combine(AppContext.BaseDirectory, "Catalog", "windows.json.gz"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaveDock"))
    {
    }

    internal MainWindow(string catalog, string dataDirectory)
    {
        InitializeComponent();
        this.dataDirectory = dataDirectory;
        scanner = new GameScanner(catalog);
        undoStore = new UndoStore(dataDirectory);
        initialized = true;
        UpdateHiddenToggle();
        Opened += InitializeAsync;
        Closing += (_, args) =>
        {
            if (writing)
            {
                args.Cancel = true;
                StatusText.Text = T("The current operation is still running.", "Der laufende Vorgang wird noch abgeschlossen.");
            }
        };
        Closed += (_, _) =>
        {
            windowClosed.Cancel();
            foreach (var icon in icons.Values)
                icon?.Dispose();
        };
    }

    private async void InitializeAsync(object? sender, EventArgs args)
    {
        Opened -= InitializeAsync;
        string? warning = null;
        try { settings = UserSettings.Load(dataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            warning = T("The saved settings could not be loaded.", "Die gespeicherten Einstellungen konnten nicht geladen werden.");
        }
        hiddenGames = new HashSet<string>((settings.HiddenGames ?? []).Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
        L.Use(settings.Language);
        UpdateTarget();
        UpdateBackupMode();
        UpdateHiddenToggle();
        await RefreshAsync(warning, refreshInstallations: true);
    }

    private string? BackupTarget => settings.Destination is not { } destination ? null :
        Path.GetFileName(Path.TrimEndingDirectorySeparator(destination)).Equals("SaveDock Backups",
            StringComparison.OrdinalIgnoreCase) ? destination : Path.Combine(destination, "SaveDock Backups");

    private void UpdateTarget() => TargetText.Text = BackupTarget ?? T("No folder selected", "Noch kein Ordner ausgewählt");

    private async void ScanClicked(object? sender, RoutedEventArgs args) => await RefreshAsync(refreshInstallations: true);

    private Task RefreshAsync(string? warning = null, bool refreshInstallations = false) =>
        WithActivityAsync(T("Scanning for game saves…", "Spielstände werden gesucht …"), async () =>
    {
        var target = BackupTarget;
        var token = windowClosed.Token;
        var custom = settings.CustomGames ?? [];
        var data = await Task.Run(() =>
        {
            var found = scanner.Scan(custom, target, token, refreshInstallations: refreshInstallations);
            var versions = target is null ? BackupHistory.Empty : new BackupStore(target).ReadHistory();
            return (found, versions, undo: undoStore.AvailableGames());
        }, token);
        games = data.found;
        history = data.versions;
        undoGames = data.undo;
        UpdateGames();
        StatusText.Text = WithHistoryWarnings(warning ?? T("Scan completed.", "Suche abgeschlossen."));
    });

    private async void TargetClicked(object? sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("Choose a game save backup folder", "Speicherort für Spielstand-Backups"),
            AllowMultiple = false
        });
        if (folders.Count == 0)
            return;

        try
        {
            var selected = folders[0].TryGetLocalPath() ?? throw new IOException(T("Choose a Windows folder.", "Wähle einen Windows-Ordner."));
            SaveSettings(settings with { Destination = selected });
            UpdateTarget();
            await RefreshAsync();
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void BackupClicked(object? sender, RoutedEventArgs args) => await BackupAsync(null);

    private async void SettingsClicked(object? sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        var selection = await new BackupSettingsDialog(settings.BackupPreferences(), settings.Language)
            .ShowDialog<SettingsSelection?>(this);
        if (selection is null)
            return;
        try
        {
            SaveSettings(settings with { Backup = selection.Backup, Language = selection.Language });
            L.Use(settings.Language);
            UpdateTarget();
            UpdateBackupMode();
            UpdateGames();
            StatusText.Text = T("Settings saved.", "Einstellungen gespeichert.");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void BackupModeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!initialized || updatingMode || BackupModeBox.SelectedIndex < 0)
            return;
        var options = settings.BackupPreferences();
        var mode = (BackupMode)BackupModeBox.SelectedIndex;
        if (mode == options.Mode || options.Retention == RetentionMode.Latest)
            return;
        try { SaveSettings(settings with { Backup = options with { Mode = mode } }); }
        catch (Exception error)
        {
            UpdateBackupMode();
            StatusText.Text = error.Message;
        }
    }

    private void UpdateBackupMode()
    {
        var options = settings.BackupPreferences();
        var single = options.Retention == RetentionMode.Latest;
        updatingMode = true;
        BackupModeBox.ItemsSource = new[]
        {
            L.T("Automatic", "Automatisch"),
            L.T("Full backup", "Vollsicherung"),
            L.T("Differential backup", "Differenzsicherung")
        };
        BackupModeBox.SelectedIndex = (int)(single ? BackupMode.Full : options.Mode);
        updatingMode = false;
        BackupModeBox.IsEnabled = !busy && !single;
        ToolTip.SetTip(BackupModeBox, single
            ? T("Keeping only the latest version retains one full backup per game.",
                "Bei „Nur neueste Version“ bleibt eine Vollsicherung pro Spiel erhalten.")
            : T("Applies to Backup and Back up all.", "Gilt für „Backup“ und „Alle sichern“."));
    }

    private async void GameBackupClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: GameRow row })
            await BackupAsync(row.Name);
    }

    private async Task BackupAsync(string? name)
    {
        var target = BackupTarget;
        if (target is null || busy || name is not null && hiddenGames.Contains(name))
            return;

        var progress = name is null ? T("Backing up game saves…", "Spielstände werden gesichert …") :
            T($"Backing up {name}…", $"{name} wird gesichert …");
        await WithActivityAsync(progress, async () =>
        {
            var custom = settings.CustomGames ?? [];
            var options = settings.BackupPreferences();
            var excluded = hiddenGames;
            var outcome = await Task.Run(() =>
            {
                var found = scanner.Scan(custom, target, CancellationToken.None, name, refreshInstallations: name is null,
                    excludedGames: excluded);
                var store = new BackupStore(target);
                var created = 0;
                var eligible = 0;
                var failures = new List<string>();
                foreach (var game in found.Where(game => game.Entries.Count > 0))
                {
                    eligible++;
                    try { if (store.Save(game, options, undoStore)) created++; }
                    catch (Exception error) { failures.Add($"{game.Name}: {error.Message}"); }
                }
                return (found, created, eligible, failures, versions: store.ReadHistory());
            });
            var retained = name is null ? games.Where(game => excluded.Contains(game.Name)) :
                games.Where(game => !game.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            games = retained.Concat(outcome.found)
                .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            history = outcome.versions;
            UpdateGames();
            StatusText.Text = WithHistoryWarnings(outcome.failures.Count > 0
                ? T($"{outcome.created} backed up · {outcome.failures.Count} failed", $"{outcome.created} gesichert · {outcome.failures.Count} fehlgeschlagen")
                    + $" · {string.Join(" · ", outcome.failures)}"
                : outcome.created > 0 ? T($"Backup complete · {outcome.created} updated", $"Sicherung abgeschlossen · {outcome.created} aktualisiert")
                : outcome.eligible > 0 ? T("Game saves were already up to date.", "Die Spielstände waren bereits aktuell.")
                : T("No local game saves to back up.", "Keine lokalen Spielstände zum Sichern gefunden."));
        }, changesFiles: true);
    }

    private async void GameLoadClicked(object? sender, RoutedEventArgs args)
    {
        if (busy || BackupTarget is not { } target || sender is not Button { DataContext: GameRow row } ||
            !history.Games.TryGetValue(row.Name, out var versions))
            return;

        var store = new BackupStore(target);
        var chosen = await new RestoreDialog(row.Name, versions, store).ShowDialog<BackupVersion?>(this);
        if (chosen is null)
            return;

        await ApplyGameChangeAsync(row.Name, target, () => store.Restore(chosen, undoStore), canRevert: true);
    }

    private async void GameRevertClicked(object? sender, RoutedEventArgs args)
    {
        if (busy || sender is not Button { DataContext: GameRow row } || !row.CanRevert)
            return;

        try
        {
            var preview = undoStore.Preview(row.Name);
            if (await new UndoDialog(preview).ShowDialog<bool>(this))
                await ApplyGameChangeAsync(row.Name, BackupTarget, () => undoStore.Revert(row.Name, preview.Id), canRevert: false);
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private Task ApplyGameChangeAsync(string name, string? target, Action apply, bool canRevert) =>
        WithActivityAsync(canRevert ? T($"Restoring {name}…", $"{name} wird wiederhergestellt …") :
            T($"Undoing the restore for {name}…", $"Laden von {name} wird rückgängig gemacht …"), async () =>
        {
            await Task.Run(apply);
            if (canRevert)
                undoGames.Add(name);
            else
                undoGames.Remove(name);

            try
            {
                var custom = settings.CustomGames ?? [];
                var updated = await Task.Run(() => scanner.Scan(custom, target, CancellationToken.None, name));
                games = games.Where(game => !game.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Concat(updated)
                    .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                StatusText.Text = canRevert ? T($"{name} was restored.", $"{name} wurde wiederhergestellt.") :
                    T($"The restore for {name} was undone.", $"Das Laden von {name} wurde rückgängig gemacht.");
            }
            finally { UpdateGames(); }
        }, changesFiles: true);

    private async void AddGameClicked(object? sender, RoutedEventArgs args) => await EditCustomAsync(null);

    private async void EditGameClicked(object? sender, RoutedEventArgs args)
    {
        if (GameList.SelectedItem is GameRow row)
            await EditCustomAsync(row.Game.Custom, row.Name);
    }

    private async Task EditCustomAsync(CustomGame? game, string? knownName = null)
    {
        if (busy)
            return;
        var current = settings.CustomGames ?? [];
        var result = await new CustomGameDialog(game, current.Select(item => item.Name), knownName)
            .ShowDialog<CustomGameEdit?>(this);
        if (result is null)
            return;
        try
        {
            var custom = current.Where(item => item != game).ToList();
            if (result.Game is not null)
                custom.Add(result.Game);
            SaveSettings(settings with { CustomGames = custom.ToArray() });
            await RefreshAsync(result.Game is null ? T("Custom game entry removed.", "Eigener Spieleintrag entfernt.") :
                T("Custom game entry saved.", "Eigener Spieleintrag gespeichert."));
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void SaveSettings(UserSettings updated)
    {
        updated.Save(dataDirectory);
        settings = updated;
    }

    private void HideGameClicked(object? sender, RoutedEventArgs args)
    {
        if (busy || GameList.SelectedItem is not GameRow row)
            return;

        var updated = new HashSet<string>(hiddenGames, StringComparer.OrdinalIgnoreCase);
        var hidden = !updated.Remove(row.Name);
        if (hidden)
            updated.Add(row.Name);
        try
        {
            SaveSettings(settings with { HiddenGames = updated.Order(StringComparer.OrdinalIgnoreCase).ToArray() });
            hiddenGames = updated;
            UpdateGames();
            UpdateActions();
            StatusText.Text = hidden ? L.T($"{row.Name} was hidden.", $"{row.Name} wurde ausgeblendet.") :
                L.T($"{row.Name} is visible again.", $"{row.Name} ist wieder sichtbar.");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void HiddenFilterChanged(object? sender, RoutedEventArgs args)
    {
        if (initialized)
            UpdateGames();
    }

    private bool ShowHidden => ShowHiddenBox.IsChecked == true;

    private void UpdateHiddenToggle() => ShowHiddenBox.Content =
        L.T($"Show hidden games ({hiddenGames.Count})", $"Ausgeblendete Spiele anzeigen ({hiddenGames.Count})");

    private async Task WithActivityAsync(string status, Func<Task> work, bool changesFiles = false)
    {
        if (busy)
            return;

        busy = true;
        writing = changesFiles;
        StatusText.Text = status;
        UpdateActions();
        try { await work(); }
        catch (OperationCanceledException) when (windowClosed.IsCancellationRequested) { }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            if (games.Count == 0 && history.Games.Count == 0)
            {
                EmptyTitle.Text = T("Operation failed", "Vorgang fehlgeschlagen");
                EmptyMessage.Text = error.Message;
            }
        }
        finally
        {
            writing = busy = false;
            UpdateActions();
        }
    }

    private void UpdateActions()
    {
        SettingsButton.IsEnabled = ScanButton.IsEnabled = TargetButton.IsEnabled = AddGameButton.IsEnabled = GameList.IsEnabled =
            EditGameButton.IsEnabled = HideGameButton.IsEnabled = ShowHiddenBox.IsEnabled = !busy;
        BackupModeBox.IsEnabled = !busy && settings.BackupPreferences().Retention != RetentionMode.Latest;
        BackupButton.IsEnabled = !busy && BackupTarget is not null &&
            games.Any(game => game.Entries.Count > 0 && !hiddenGames.Contains(game.Name));
        Activity.IsVisible = busy;
    }

    private string WithHistoryWarnings(string status) => history.Warnings.Count == 0 ? status :
        $"{status} · {T("Some backups could not be read", "Einige Sicherungen konnten nicht gelesen werden")}: " +
        string.Join(" · ", history.Warnings);

    private void UpdateGames()
    {
        OverviewText.Text = T("Local game saves and stored backups", "Lokale Spielstände und gespeicherte Sicherungen");
        var visible = games.Where(game => ShowHidden || !hiddenGames.Contains(game.Name)).ToArray();
        GameCount.Text = games.Select(game => game.Name).Concat(history.Games.Keys).Concat(undoGames)
            .Concat(ShowHidden ? hiddenGames : [])
            .Distinct(StringComparer.OrdinalIgnoreCase).Count(name => ShowHidden || !hiddenGames.Contains(name))
            .ToString("N0", L.Culture);
        EntryCount.Text = visible.Sum(game => game.Entries.Count).ToString("N0", L.Culture);
        TotalSize.Text = SizeText.Format(visible.Sum(game => game.Bytes));
        UpdateHiddenToggle();
        UpdateList();
    }

    private void UpdateList()
    {
        var selectedName = (GameList.SelectedItem as GameRow)?.Name;
        var query = SearchBox.Text?.Trim() ?? "";
        var current = games.ToDictionary(game => game.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in history.Games.Keys.Concat(undoGames).Concat(ShowHidden ? hiddenGames : []))
            current.TryAdd(name, new GameSave(name, [], null, null));

        var rows = current.Values.Where(game => ShowHidden || !hiddenGames.Contains(game.Name))
            .Where(game => game.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                game.Launchers?.Any(launcher => launcher.Contains(query, StringComparison.CurrentCultureIgnoreCase)) == true)
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(game => new GameRow(game, history.Games.GetValueOrDefault(game.Name)?.FirstOrDefault(),
                BackupTarget is not null, undoGames.Contains(game.Name), hiddenGames.Contains(game.Name),
                GetIcon(game.IconPath))).ToArray();
        GameList.ItemsSource = rows;
        GameList.SelectedItem = rows.FirstOrDefault(row => row.Name == selectedName);
        EmptyState.IsVisible = rows.Length == 0;
        if (current.Count == 0)
        {
            EmptyTitle.Text = T("No games found", "Keine Spiele gefunden");
            EmptyMessage.Text = T("Use Add game to select your own save paths.",
                "Über „Spiel hinzufügen“ kannst du eigene Speicherpfade auswählen.");
        }
        else if (query.Length == 0 && rows.Length == 0 && !ShowHidden)
        {
            EmptyTitle.Text = T("All detected games are hidden", "Alle gefundenen Spiele sind ausgeblendet");
            EmptyMessage.Text = T("Select Show hidden games to see them.", "Wähle „Ausgeblendete Spiele anzeigen“, um sie zu sehen.");
        }
        else
        {
            EmptyTitle.Text = T("No matching games", "Keine passenden Spiele");
            EmptyMessage.Text = T("Try a different search.", "Versuche es mit einem anderen Suchbegriff.");
        }
        UpdateDetail();
    }

    private Bitmap? GetIcon(string? path)
    {
        if (path is null)
            return null;
        if (!icons.TryGetValue(path, out var icon))
            icons[path] = icon = GameIcons.Read(path);
        return icon;
    }

    private void SearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (initialized)
            UpdateList();
    }

    private void GameSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (initialized)
            UpdateDetail();
    }

    private void UpdateDetail()
    {
        var row = GameList.SelectedItem as GameRow;
        DetailTitle.Text = row?.Name ?? T("Select a game.", "Wähle ein Spiel aus.");
        EditGameButton.IsVisible = row is not null;
        HideGameButton.IsVisible = row is not null;
        HideGameButton.Content = row?.IsHidden == true ? L.T("Unhide game", "Spiel einblenden") :
            L.T("Hide game", "Spiel ausblenden");
        EditGameButton.Content = row?.Game.Custom is not null ? T("Edit custom paths", "Eigene Pfade bearbeiten") :
            T("Add save path", "Spielstandpfad hinzufügen");
        DetailList.ItemsSource = row?.Game.Entries;
    }
}

public sealed class GameRow
{
    internal GameRow(GameSave game, BackupVersion? backup, bool hasTarget, bool canRevert, bool isHidden, Bitmap? icon)
    {
        Game = game;
        Icon = icon;
        CanBackup = hasTarget && !isHidden && game.Entries.Count > 0;
        CanLoad = backup is not null;
        CanRevert = canRevert;
        IsHidden = isHidden;
        var count = game.Entries.Count;
        var entryWord = count == 1 ? T("entry", "Eintrag") : T("entries", "Einträge");
        EntrySummary = count == 0 ? T("No saves found", "Keine Spielstände gefunden") :
            $"{count.ToString("N0", L.Culture)} {entryWord} · {SizeText.Format(game.Bytes)}";
        if (game.Launchers is { Length: > 0 } launchers)
            EntrySummary = $"{string.Join(", ", launchers)} · {EntrySummary}";
        if (backup is not null)
            EntrySummary += $" · {T("Backed up", "Gesichert am")} {backup.When.ToLocalTime().ToString("g", L.Culture)}";
        if (isHidden)
            EntrySummary = $"{L.T("Hidden", "Ausgeblendet")} · {EntrySummary}";
    }

    internal GameSave Game { get; }
    public string Name => Game.Name;
    public string Monogram => string.Concat(Name.Where(char.IsLetterOrDigit).Take(2)).ToUpperInvariant();
    public bool CanBackup { get; }
    public bool CanLoad { get; }
    public bool CanRevert { get; }
    internal bool IsHidden { get; }
    public bool HasNoIcon => Icon is null;
    public Bitmap? Icon { get; }
    public string EntrySummary { get; }
}
