using System.Globalization;
using Avalonia;

namespace SaveDock;

internal enum AppLanguage { English, German }

internal static class L
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static bool german;

    private static readonly (string Key, string English, string German)[] WindowText =
    [
        ("SettingsText", "Settings", "Einstellungen"),
        ("RescanText", "Scan again", "Erneut suchen"),
        ("BackupAllText", "Back up all", "Alle sichern"),
        ("OverviewTitle", "Your game saves", "Deine Spielstände"),
        ("ScanningText", "Looking for local game saves…", "Lokale Spielstände werden gesucht …"),
        ("GamesText", "Games", "Spiele"),
        ("EntriesText", "Entries", "Einträge"),
        ("SizeLabel", "File size", "Dateigröße"),
        ("GameListText", "Detected games", "Gefundene Spiele"),
        ("GameFilterText", "Game or launcher", "Spiel oder Launcher"),
        ("GameFilterName", "Filter by game or launcher", "Nach Spiel oder Launcher filtern"),
        ("AddGameText", "+ Add game", "+ Spiel hinzufügen"),
        ("BackupText", "Backup", "Backup"),
        ("LoadText", "Restore", "Laden"),
        ("UndoText", "Undo restore", "Rückgängig"),
        ("UndoTip", "Recover the state from before the last restore", "Zustand vor dem letzten Laden wiederherstellen"),
        ("InitialScanText", "The first scan may take a moment.", "Der erste Scan kann einen Moment dauern."),
        ("DestinationText", "Backup destination", "Sicherungsziel"),
        ("SelectFolderText", "Choose folder", "Ordner auswählen"),
        ("BackupModeText", "Backup mode", "Sicherungsart"),
        ("SavePathsText", "Save paths", "Spielstandpfade"),
        ("ReadyText", "Ready", "Bereit")
    ];

    public static CultureInfo Culture => german ? German : English;
    public static string T(string english, string deutsch) => german ? deutsch : english;

    public static void Use(AppLanguage language, Application? application = null)
    {
        german = language == AppLanguage.German;
        var resources = (application ?? Application.Current)?.Resources;
        if (resources is null)
            return;
        foreach (var item in WindowText)
            resources[item.Key] = T(item.English, item.German);
    }
}
