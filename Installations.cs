using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SaveDock;

internal sealed partial class Installations(IReadOnlyList<SteamRoot> steamRoots, IReadOnlyList<InstalledGame> games)
{
    private readonly ILookup<string, InstalledGame> byName = games.ToLookup(game => Key(game.Name));
    private readonly ILookup<(string Store, string? Id), InstalledGame> byId =
        games.Where(game => game.Id is not null).ToLookup(game => (game.Store, game.Id));

    public IReadOnlyList<SteamRoot> SteamRoots { get; } = steamRoots;
    public IReadOnlyList<InstalledGame> Games { get; } = games;

    public IReadOnlyList<InstalledGame> Match(CatalogGame game) =>
        game.Steam.SelectMany(id => byId[("steam", id)])
            .Concat(game.Gog.SelectMany(id => byId[("gog", id)]))
            .Concat(game.Names.SelectMany(name => byName[Key(name)]))
            .Where(install => install.Store switch
            {
                "steam" => game.Steam.Length == 0 || game.Steam.Contains(install.Id),
                "gog" => game.Gog.Length == 0 || game.Gog.Contains(install.Id),
                _ => true
            })
            .DistinctBy(install => install.Directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static Installations Discover()
    {
        var roots = SteamDirectories().ToArray();
        var steam = roots.SelectMany(root => SteamGames(root.Libraries));
        var epicData = Path.Combine(SavePaths.WindowsFolders["winProgramData"], "Epic");
        var epicManifestPath = ReadRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath");
        var games = steam.Concat(EpicGames(epicData, epicManifestPath)).Concat(XboxGames()).Concat(RegistryGames())
            .GroupBy(game => game.Directory, StringComparer.OrdinalIgnoreCase)
            .SelectMany(directory => directory.Any(game => game.Store != "other")
                ? directory.Where(game => game.Store != "other").DistinctBy(game => game.Store)
                : directory.DistinctBy(game => Key(game.Name)))
            .ToArray();
        return new Installations(roots, games);
    }

    public static string Key(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var character in name)
            if (char.IsLetterOrDigit(character))
                result.Append(char.ToUpperInvariant(character));
        return result.ToString();
    }

    private static IEnumerable<SteamRoot> SteamDirectories()
    {
        string?[] candidates =
        [
            ReadRegistry(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
            ReadRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        ];
        foreach (var candidate in candidates.Where(path => Directory.Exists(path))
                     .Select(path => Path.GetFullPath(path!)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libraries = new[] { candidate }.Concat(
                ReadVdf(Path.Combine(candidate, "steamapps", "libraryfolders.vdf"))
                    .Where(pair => pair.Key.Equals("path", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value))
                .Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var users = SavePaths.Enumerate(Path.Combine(candidate, "userdata"), "*", directories: true)
                .Select(Path.GetFileName).OfType<string>().Where(name => name.All(char.IsAsciiDigit)).ToArray();
            yield return new SteamRoot(candidate, libraries, users);
        }
    }

    private static IEnumerable<InstalledGame> SteamGames(IEnumerable<string> libraries)
    {
        foreach (var library in libraries)
        foreach (var file in SavePaths.Enumerate(Path.Combine(library, "steamapps"), "appmanifest_*.acf", false))
        {
            var values = ReadVdf(file).ToLookup(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var directory = values["installdir"].FirstOrDefault();
            var name = values["name"].FirstOrDefault();
            var id = values["appid"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name) || id == "228980")
                continue;
            var install = Path.Combine(library, "steamapps", "common", directory);
            if (Directory.Exists(install))
                yield return new InstalledGame(name, "steam", id, install, null);
        }
    }

    private static IEnumerable<InstalledGame> RegistryGames()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var local = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            foreach (var entry in ReadInstallKeys(local, @"SOFTWARE\GOG.com\Games", "gog"))
                yield return entry;
            foreach (var entry in ReadInstallKeys(local, @"SOFTWARE\Ubisoft\Launcher\Installs", "uplay"))
                yield return entry;
            foreach (var entry in ReadInstallKeys(local, @"SOFTWARE\EA Games", "ea")
                .Concat(ReadInstallKeys(local, @"SOFTWARE\Electronic Arts\EA Games", "ea")))
                yield return entry;
            const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (var entry in ReadInstallKeys(local, uninstall, "other").Concat(ReadInstallKeys(user, uninstall, "other")))
                yield return entry;
        }
    }

    private static IEnumerable<InstalledGame> ReadInstallKeys(RegistryKey root, string path, string store)
    {
        using var games = TryOpen(root, path);
        foreach (var subkey in games?.GetSubKeyNames() ?? [])
        {
            using var game = TryOpen(games!, subkey);
            var directory = store switch
            {
                "gog" => TextValue(game, "path", "InstallLocation"),
                "uplay" => TextValue(game, "InstallDir", "InstallLocation"),
                "ea" => TextValue(game, "Install Dir", "InstallDir", "InstallLocation"),
                _ => TextValue(game, "InstallLocation")
            };
            var icon = ReadDisplayIcon(TextValue(game, "DisplayIcon"));
            directory = ExistingDirectory(directory) ?? (store == "other" && icon is not null
                ? ExistingDirectory(Path.GetDirectoryName(icon)) : null);
            if (directory is null)
                continue;

            var name = TextValue(game, "DisplayName", "gameName") ??
                (store == "other" ? null : Path.GetFileName(directory));
            if (name is null || LauncherNames.Contains(name))
                continue;

            var publisher = TextValue(game, "Publisher") ?? "";
            var uninstall = TextValue(game, "UninstallString") ?? "";
            var battleNet = BattleNetProduct().Match(uninstall);
            var provider = store != "other" ? store
                : battleNet.Success || publisher.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase) ? "battlenet"
                : publisher.Equals("Electronic Arts", StringComparison.OrdinalIgnoreCase) ||
                    uninstall.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase) ? "ea"
                : uninstall.Contains("uplay://", StringComparison.OrdinalIgnoreCase) ? "uplay"
                : "other";
            var id = provider switch
            {
                "gog" => TextValue(game, "gameID") ?? subkey,
                "uplay" when store == "uplay" => subkey,
                "battlenet" when battleNet.Success => battleNet.Groups[1].Value,
                _ => null
            };
            var providerRoot = provider == "uplay" ? UbisoftRoot() : null;
            var saveFolders = providerRoot is not null && id is not null && id.All(char.IsAsciiDigit)
                ? new[] { Path.Combine(providerRoot, "savegames", "*", id) } : null;
            var exe = ExistingGameFile(directory, TextValue(game, "exe", "LaunchExecutable")) ?? icon;
            yield return new InstalledGame(name, provider, id, directory, exe, providerRoot, saveFolders);
        }
    }

    private static RegistryKey? TryOpen(RegistryKey root, string path)
    {
        try { return root.OpenSubKey(path); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static string? ReadRegistry(string path, string name)
    {
        try { return Registry.GetValue(path, name, null) as string; }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadVdf(string file)
    {
        try
        {
            return VdfPair().Matches(File.ReadAllText(file)).Select(match => KeyValuePair.Create(
                match.Groups[1].Value, match.Groups[2].Value.Replace(@"\\", @"\").Replace("\\\"", "\""))).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex("""(?m)^\s*"([^"\r\n]+)"[ \t]+"((?:\\.|[^"\\\r\n])*)"[ \t]*\r?$""")]
    private static partial Regex VdfPair();

    [GeneratedRegex("""Battle\.net.*?--uid(?:=|\s+)"?([a-z0-9_-]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex BattleNetProduct();
}

internal sealed record SteamRoot(string Path, string[] Libraries, string[] Users);
internal sealed record InstalledGame(string Name, string Store, string? Id, string Directory, string? Executable,
    string? ProviderRoot = null, string[]? SaveFolders = null);
