using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace SaveDock;

public partial class MainWindow : Window
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private readonly string dataDirectory;
    private readonly GameScanner scanner;
    private readonly CancellationTokenSource windowClosed = new();
    private readonly Dictionary<string, Bitmap?> icons = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GameSave> games = [];
    private BackupHistory history = BackupHistory.Empty;
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
        BackupModeBox.ItemsSource = new[] { "Automatisch", "Vollsicherung", "Differenzsicherung" };
        initialized = true;
        Opened += InitializeAsync;
        Closing += (_, args) =>
        {
            if (writing)
            {
                args.Cancel = true;
                StatusText.Text = "Der laufende Vorgang wird noch abgeschlossen.";
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
            warning = "Die gespeicherten Einstellungen konnten nicht geladen werden.";
        }
        UpdateTarget();
        UpdateBackupMode();
        await RefreshAsync(warning, refreshInstallations: true);
    }

    private string? BackupTarget => settings.Destination is not { } destination ? null :
        Path.GetFileName(Path.TrimEndingDirectorySeparator(destination)).Equals("SaveDock Backups",
            StringComparison.OrdinalIgnoreCase) ? destination : Path.Combine(destination, "SaveDock Backups");

    private void UpdateTarget() => TargetText.Text = BackupTarget ?? "Noch kein Ordner ausgewählt";

    private async void ScanClicked(object? sender, RoutedEventArgs args) => await RefreshAsync(refreshInstallations: true);

    private Task RefreshAsync(string? warning = null, bool refreshInstallations = false) =>
        WithActivityAsync("Spielstände werden gesucht …", async () =>
    {
        var target = BackupTarget;
        var token = windowClosed.Token;
        var custom = settings.CustomGames ?? [];
        var data = await Task.Run(() =>
        {
            var found = scanner.Scan(custom, target, token, refreshInstallations: refreshInstallations);
            var versions = target is null ? BackupHistory.Empty : new BackupStore(target).ReadHistory();
            return (found, versions);
        }, token);
        games = data.found;
        history = data.versions;
        UpdateGames();
        StatusText.Text = WithHistoryWarnings(warning ?? "Suche abgeschlossen.");
    });

    private async void TargetClicked(object? sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Speicherort für Spielstand-Backups",
            AllowMultiple = false
        });
        if (folders.Count == 0)
            return;

        try
        {
            var selected = folders[0].TryGetLocalPath() ?? throw new IOException("Wähle einen Windows-Ordner.");
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
        var options = await new BackupSettingsDialog(settings.BackupPreferences()).ShowDialog<BackupOptions?>(this);
        if (options is null)
            return;
        try
        {
            SaveSettings(settings with { Backup = options });
            UpdateBackupMode();
            StatusText.Text = "Sicherungseinstellungen gespeichert.";
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
        BackupModeBox.SelectedIndex = (int)(single ? BackupMode.Full : options.Mode);
        updatingMode = false;
        BackupModeBox.IsEnabled = !busy && !single;
        ToolTip.SetTip(BackupModeBox, single ? "Bei „Nur neueste Version“ bleibt eine Vollsicherung pro Spiel erhalten." :
            "Gilt für „Backup“ und „Alle sichern“.");
    }

    private async void GameBackupClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: GameRow row })
            await BackupAsync(row.Name);
    }

    private async Task BackupAsync(string? name)
    {
        var target = BackupTarget;
        if (target is null || busy)
            return;

        await WithActivityAsync(name is null ? "Spielstände werden gesichert …" : $"{name} wird gesichert …", async () =>
        {
            var custom = settings.CustomGames ?? [];
            var options = settings.BackupPreferences();
            var outcome = await Task.Run(() =>
            {
                var found = scanner.Scan(custom, target, CancellationToken.None, name, refreshInstallations: name is null);
                var store = new BackupStore(target);
                var created = 0;
                var eligible = 0;
                var failures = new List<string>();
                foreach (var game in found.Where(game => game.Entries.Count > 0))
                {
                    eligible++;
                    try { if (store.Save(game, options)) created++; }
                    catch (Exception error) { failures.Add($"{game.Name}: {error.Message}"); }
                }
                return (found, created, eligible, failures, versions: store.ReadHistory());
            });
            games = name is null ? outcome.found : games
                .Where(game => !game.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Concat(outcome.found)
                .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            history = outcome.versions;
            UpdateGames();
            StatusText.Text = WithHistoryWarnings(outcome.failures.Count > 0
                ? $"{outcome.created} gesichert · {outcome.failures.Count} fehlgeschlagen · {string.Join(" · ", outcome.failures)}"
                : outcome.created > 0 ? $"Sicherung abgeschlossen · {outcome.created} aktualisiert"
                : outcome.eligible > 0 ? "Die Spielstände waren bereits aktuell."
                : "Keine lokalen Spielstände zum Sichern gefunden.");
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

        await WithActivityAsync($"{row.Name} wird wiederhergestellt …", async () =>
        {
            var custom = settings.CustomGames ?? [];
            var updated = await Task.Run(() =>
            {
                store.Restore(chosen);
                return scanner.Scan(custom, target, CancellationToken.None, row.Name);
            });
            games = games.Where(game => !game.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase)).Concat(updated)
                .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            UpdateGames();
            StatusText.Text = $"{row.Name} wurde wiederhergestellt.";
        }, changesFiles: true);
    }

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
            await RefreshAsync(result.Game is null ? "Eigener Spieleintrag entfernt." : "Eigener Spieleintrag gespeichert.");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void SaveSettings(UserSettings updated)
    {
        updated.Save(dataDirectory);
        settings = updated;
    }

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
                EmptyTitle.Text = "Vorgang fehlgeschlagen";
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
            EditGameButton.IsEnabled = !busy;
        BackupModeBox.IsEnabled = !busy && settings.BackupPreferences().Retention != RetentionMode.Latest;
        BackupButton.IsEnabled = !busy && BackupTarget is not null && games.Any(game => game.Entries.Count > 0);
        Activity.IsVisible = busy;
    }

    private string WithHistoryWarnings(string status) => history.Warnings.Count == 0 ? status :
        $"{status} · Einige Sicherungen konnten nicht gelesen werden: {string.Join(" · ", history.Warnings)}";

    private void UpdateGames()
    {
        OverviewText.Text = "Lokale Spielstände und gespeicherte Sicherungen";
        GameCount.Text = games.Select(game => game.Name).Union(history.Games.Keys, StringComparer.OrdinalIgnoreCase)
            .Count().ToString("N0", German);
        EntryCount.Text = games.Sum(game => game.Entries.Count).ToString("N0", German);
        TotalSize.Text = SizeText.Format(games.Sum(game => game.Bytes));
        UpdateList();
    }

    private void UpdateList()
    {
        var selectedName = (GameList.SelectedItem as GameRow)?.Name;
        var query = SearchBox.Text?.Trim() ?? "";
        var current = games.ToDictionary(game => game.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in history.Games.Keys)
            current.TryAdd(name, new GameSave(name, [], null, null));

        var rows = current.Values.Where(game => game.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                game.Launchers?.Any(launcher => launcher.Contains(query, StringComparison.CurrentCultureIgnoreCase)) == true)
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(game => new GameRow(game, history.Games.GetValueOrDefault(game.Name)?.FirstOrDefault(),
                BackupTarget is not null, GetIcon(game.IconPath))).ToArray();
        GameList.ItemsSource = rows;
        GameList.SelectedItem = rows.FirstOrDefault(row => row.Name == selectedName);
        EmptyState.IsVisible = rows.Length == 0;
        EmptyTitle.Text = current.Count == 0 ? "Keine Spiele gefunden" : "Keine passenden Spiele";
        EmptyMessage.Text = current.Count == 0
            ? "Über „Spiel hinzufügen“ kannst du eigene Speicherpfade auswählen."
            : "Versuche es mit einem anderen Suchbegriff.";
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
        DetailTitle.Text = row?.Name ?? "Wähle ein Spiel aus.";
        EditGameButton.IsVisible = row is not null;
        EditGameButton.Content = row?.Game.Custom is not null ? "Eigene Pfade bearbeiten" : "Spielstandpfad hinzufügen";
        DetailList.ItemsSource = row?.Game.Entries;
    }
}

public sealed class GameRow
{
    internal GameRow(GameSave game, BackupVersion? backup, bool hasTarget, Bitmap? icon)
    {
        Game = game;
        Icon = icon;
        CanBackup = hasTarget && game.Entries.Count > 0;
        CanLoad = backup is not null;
        EntrySummary = game.Entries.Count == 0 ? "Keine Spielstände gefunden" :
            $"{game.Entries.Count} {(game.Entries.Count == 1 ? "Eintrag" : "Einträge")} · {SizeText.Format(game.Bytes)}";
        if (game.Launchers is { Length: > 0 } launchers)
            EntrySummary = $"{string.Join(", ", launchers)} · {EntrySummary}";
        if (backup is not null)
            EntrySummary += $" · Gesichert am {backup.When.ToLocalTime():dd.MM.yyyy HH:mm}";
    }

    internal GameSave Game { get; }
    public string Name => Game.Name;
    public string Monogram => string.Concat(Name.Where(char.IsLetterOrDigit).Take(2)).ToUpperInvariant();
    public bool CanBackup { get; }
    public bool CanLoad { get; }
    public bool HasNoIcon => Icon is null;
    public Bitmap? Icon { get; }
    public string EntrySummary { get; }
}
