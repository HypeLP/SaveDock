using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaveDock;

internal sealed partial class GameScanner(string catalogPath)
{
    private CatalogGame[]? catalog;
    private ILookup<string, CatalogGame>? catalogByName;
    private Installations? cachedInstallations;

    public IReadOnlyList<GameSave> Scan(CustomGame[] customGames, string? backupTarget, CancellationToken token,
        string? onlyGame = null, Installations? detected = null, bool refreshInstallations = false,
        IReadOnlySet<string>? excludedGames = null)
    {
        catalog ??= ReadCatalog();
        var installations = detected ?? (refreshInstallations || cachedInstallations is null
            ? cachedInstallations = Installations.Discover() : cachedInstallations);
        var definitions = onlyGame is null ? catalog :
            (catalogByName ??= catalog.ToLookup(game => game.Name, StringComparer.OrdinalIgnoreCase))[onlyGame];
        var exclusion = backupTarget is null ? null : new PathBoundary(backupTarget);
        var probes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<InstalledGame>();
        var result = new List<GameSave>();
        var custom = customGames
            .Where(game => excludedGames?.Contains(game.Name) != true &&
                (onlyGame is null || game.Name.Equals(onlyGame, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(game => game.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            token.ThrowIfCancellationRequested();
            if (excludedGames?.Contains(definition.Name) == true)
            {
                matched.UnionWith(installations.Match(definition));
                continue;
            }
            var matches = installations.Match(definition);
            matched.UnionWith(matches);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in definition.Files)
            foreach (var path in Expand(rule, definition, matches, installations.SteamRoots))
            {
                if (!probes.TryGetValue(path, out var files))
                    probes[path] = files = SavePaths.FindFiles(path, exclusion, token).ToArray();
                paths.UnionWith(files);
            }

            var registry = definition.Registry.Select(rule => rule.Path).Where(RegistryData.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var own = custom.Remove(definition.Name, out var customGame) ? customGame : null;
            AddLocalPaths(paths, matches, own, exclusion, token);

            var entries = ReadFiles(paths).Concat(registry.Select(path => new SaveEntry(path, 0, true))).ToArray();
            if (entries.Length > 0 || matches.Count > 0 || own is not null)
            {
                var icon = OwnIcon(own) ?? FindIcon(definition, matches, installations.SteamRoots, token);
                result.Add(new GameSave(definition.Name, entries, icon, own, LauncherNames(matches)));
            }
        }

        var unmatched = installations.Games.Where(game => !matched.Contains(game) && game.Store != "other" &&
                excludedGames?.Contains(game.Name) != true &&
                (onlyGame is null || game.Name.Equals(onlyGame, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(game => game.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var group in unmatched)
        {
            token.ThrowIfCancellationRequested();
            var installs = group.ToArray();
            var own = custom.Remove(group.Key, out var customGame) ? customGame : null;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddLocalPaths(paths, installs, own, exclusion, token);
            var icon = OwnIcon(own) ?? installs.Select(install => FindInstalledIcon(install, [group.Key])).FirstOrDefault(File.Exists);
            var launchers = LauncherNames(installs);
            var index = result.FindIndex(game => game.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                result.Add(new GameSave(group.Key, ReadFiles(paths).ToArray(), icon, own, launchers));
            else
            {
                var previous = result[index];
                result[index] = previous with
                {
                    Entries = previous.Entries.Concat(ReadFiles(paths))
                        .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                    IconPath = previous.IconPath ?? icon,
                    Launchers = (previous.Launchers ?? []).Union(launchers).ToArray()
                };
            }
        }

        foreach (var game in custom.Values)
        {
            var paths = game.Paths.SelectMany(path => SavePaths.FindLiteralFiles(path, exclusion, token))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            result.Add(new GameSave(game.Name, ReadFiles(paths).ToArray(), OwnIcon(game), game));
        }

        return result.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static void AddLocalPaths(HashSet<string> paths, IEnumerable<InstalledGame> installs, CustomGame? custom,
        PathBoundary? exclusion, CancellationToken token)
    {
        foreach (var path in installs.SelectMany(install => install.SaveFolders ?? []))
            paths.UnionWith(SavePaths.FindFiles(path, exclusion, token));
        foreach (var path in custom?.Paths ?? [])
            paths.UnionWith(SavePaths.FindLiteralFiles(path, exclusion, token));
    }

    private static string? OwnIcon(CustomGame? game) => File.Exists(game?.IconPath) ? game!.IconPath : null;

    private static string[] LauncherNames(IEnumerable<InstalledGame> installs) =>
        installs.Select(game => game.Store switch
        {
            "steam" => "Steam",
            "epic" => "Epic Games",
            "gog" => "GOG",
            "uplay" => "Ubisoft Connect",
            "ea" => "EA app",
            "battlenet" => "Battle.net",
            "microsoft" => "Xbox / Microsoft Store",
            _ => null
        }).OfType<string>().Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToArray();

    private CatalogGame[] ReadCatalog()
    {
        using var file = File.OpenRead(catalogPath);
        using var content = new GZipStream(file, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<CatalogGame[]>(content, JsonSerializerOptions.Web)
            ?? throw new IOException(L.T("The game catalog could not be loaded.", "Der Spielekatalog konnte nicht geladen werden."));
    }

    private static IEnumerable<SaveEntry> ReadFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            if (info.Exists)
                yield return new SaveEntry(info.FullName, info.Length, false);
        }
    }

    private static string? FindIcon(CatalogGame definition, IReadOnlyList<InstalledGame> installed,
        IReadOnlyList<SteamRoot> steamRoots, CancellationToken token)
    {
        foreach (var game in installed)
        {
            if (game.Executable is not null && File.Exists(game.Executable))
                return game.Executable;

            foreach (var rule in definition.Launch)
            foreach (var path in Expand(rule, definition, [game], steamRoots))
            {
                var exe = SavePaths.FindFiles(path, null, token).FirstOrDefault();
                if (exe is not null)
                    return exe;
            }

            if (FindInstalledIcon(game, definition.Names) is { } fallback)
                return fallback;
        }

        foreach (var root in steamRoots)
        foreach (var id in definition.Steam)
        {
            var cache = Path.Combine(root.Path, "appcache", "librarycache");
            var icon = Path.Combine(cache, id + "_icon.jpg");
            if (File.Exists(icon))
                return icon;
        }
        return null;
    }

    private static string? FindInstalledIcon(InstalledGame game, IReadOnlyList<string> names)
    {
        if (File.Exists(game.Executable))
            return game.Executable;

        var executables = SavePaths.Enumerate(game.Directory, "*.exe", directories: false)
            .Where(path => !HelperExecutable().IsMatch(Path.GetFileNameWithoutExtension(path))).Take(12).ToArray();
        return executables.FirstOrDefault(path => names.Any(name =>
            Installations.Key(name) == Installations.Key(Path.GetFileNameWithoutExtension(path)))) ??
            (executables.Length == 1 ? executables[0] : null);
    }

    private static IEnumerable<string> Expand(CatalogRule rule, CatalogGame game, IReadOnlyList<InstalledGame> installed,
        IReadOnlyList<SteamRoot> steamRoots)
    {
        var path = rule.Path;
        var hasBase = path.Contains("<base>", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("<game>", StringComparison.OrdinalIgnoreCase);
        var hasRoot = path.Contains("<root>", StringComparison.OrdinalIgnoreCase);
        var hasId = path.Contains("<storeGameId>", StringComparison.OrdinalIgnoreCase);
        var hasUser = path.Contains("<storeUserId>", StringComparison.OrdinalIgnoreCase);

        if (!hasBase && !hasRoot && !hasId)
        {
            var local = Fill(path, null, null, null, hasUser ? "*" : null);
            if (local is not null)
                yield return local;
            yield break;
        }

        if (!hasBase && Allows(rule, "steam"))
            foreach (var steam in steamRoots)
            foreach (var id in hasId ? game.Steam : [""])
            foreach (var user in hasUser ? steam.Users : [""])
            {
                var candidate = Fill(path, null, steam.Path, id, user);
                if (candidate is not null)
                    yield return candidate;
            }

        foreach (var install in installed.Where(install => Allows(rule, install.Store)))
        {
            var steam = steamRoots.FirstOrDefault(root => SavePaths.IsWithin(install.Directory, root.Path));
            foreach (var user in hasUser ? steam?.Users ?? ["*"] : [""])
            {
                var candidate = Fill(path, install.Directory, install.ProviderRoot ?? steam?.Path ?? Path.GetDirectoryName(install.Directory),
                    install.Id, user);
                if (candidate is not null)
                    yield return candidate;
            }
        }
    }

    private static bool Allows(CatalogRule rule, string store) => rule.Stores.Length == 0 || rule.Stores.Contains(store);

    private static string? Fill(string template, string? directory, string? root, string? id, string? user)
    {
        var unresolved = false;
        var result = Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            var value = name switch
            {
                "base" => directory,
                "root" => root,
                "game" => directory is null ? null : Path.GetFileName(directory),
                "storeGameId" => string.IsNullOrEmpty(id) ? null : id,
                "storeUserId" => string.IsNullOrEmpty(user) ? null : user,
                _ => SavePaths.WindowsFolders.GetValueOrDefault(name)
            };
            if (string.IsNullOrEmpty(value))
                unresolved = true;
            return value ?? "";
        });
        return unresolved ? null : result;
    }

    [GeneratedRegex("<([^<>]+)>")]
    private static partial Regex Placeholder();

    [GeneratedRegex("setup|uninstall|crash|redist|helper|report|update|easyanticheat", RegexOptions.IgnoreCase)]
    private static partial Regex HelperExecutable();
}

internal sealed record CatalogGame(string Name, string[] Names, string[] Steam, string[] Gog,
    CatalogRule[] Files, CatalogRule[] Registry, CatalogRule[] Launch);
internal sealed record CatalogRule(string Path, string[] Stores);
