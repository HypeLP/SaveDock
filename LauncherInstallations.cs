using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SaveDock;

internal sealed partial class Installations
{
    private static readonly JsonDocumentOptions LauncherJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HashSet<string> LauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Battle.net", "Battle.net App", "Blizzard App", "EA app", "EA Desktop", "Origin",
        "Ubisoft Connect", "Uplay", "GOG Galaxy", "Epic Games Launcher", "Epic Online Services"
    };

    internal static IEnumerable<InstalledGame> EpicGames(string epicData, string? configuredManifestPath = null)
    {
        var usual = Path.Combine(epicData, "EpicGamesLauncher", "Data", "Manifests");
        var folders = new List<string>();
        if (ExistingDirectory(configuredManifestPath) is { } configured)
        {
            folders.Add(configured);
            folders.Add(Path.Combine(configured, "Manifests"));
            folders.Add(Path.Combine(configured, "Data", "Manifests"));
        }
        folders.Add(usual);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var file in SavePaths.Enumerate(folder, "*.item", false))
        {
            using var manifest = ReadLauncherJson(file);
            if (manifest is null)
                continue;
            var data = manifest.RootElement;
            var id = GetText(data, "AppName");
            if (ExistingDirectory(GetText(data, "InstallLocation")) is not { } directory)
                continue;

            // Keep DLC, incomplete downloads and editor installs out of the game list.
            var parent = GetText(data, "MainGameAppName");
            var hasOtherParent = !string.IsNullOrEmpty(parent) && !string.Equals(parent, id, StringComparison.OrdinalIgnoreCase);
            var isGame = !data.TryGetProperty("AppCategories", out var categories) ||
                categories.ValueKind != JsonValueKind.Array ||
                categories.GetArrayLength() == 0 ||
                categories.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
                    string.Equals(item.GetString(), "games", StringComparison.OrdinalIgnoreCase));
            if (hasOtherParent || !isGame ||
                data.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
            {
                seen.Add(directory);
                continue;
            }

            if (EpicGame(data, directory) is { } game && seen.Add(directory))
                yield return game;
        }

        using var installed = ReadLauncherJson(Path.Combine(epicData, "UnrealEngineLauncher", "LauncherInstalled.dat"));
        if (installed is null || !installed.RootElement.TryGetProperty("InstallationList", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var data in list.EnumerateArray())
            if (ExistingDirectory(GetText(data, "InstallLocation")) is { } directory &&
                EpicGame(data, directory) is { } game && seen.Add(directory))
                yield return game;
    }

    private static InstalledGame? EpicGame(JsonElement data, string directory)
    {
        var id = GetText(data, "AppName");
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith("UE_", StringComparison.OrdinalIgnoreCase))
            return null;
        var title = GetText(data, "DisplayName");
        var name = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(directory) : title;
        return string.IsNullOrWhiteSpace(name) ? null :
            new InstalledGame(name, "epic", id, directory, ExistingGameFile(directory, GetText(data, "LaunchExecutable")));
    }

    private static JsonDocument? ReadLauncherJson(string path)
    {
        try
        {
            var document = JsonDocument.Parse(File.ReadAllText(path), LauncherJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;
            document.Dispose();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? GetText(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<InstalledGame> XboxGames()
    {
        var roots = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .SelectMany(drive => XboxRoots(drive.RootDirectory.FullName)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        foreach (var directory in SavePaths.Enumerate(root, "*", directories: true))
            if (ReadXboxGame(directory, SavePaths.WindowsFolders["winLocalAppData"]) is { } game)
                yield return game;
    }

    private static IEnumerable<string> XboxRoots(string root)
    {
        yield return Path.Combine(root, "XboxGames");
        var marker = Path.Combine(root, ".GamingRoot");
        byte[] content;
        try
        {
            if (new FileInfo(marker).Length is < 10 or > 32768)
                yield break;
            content = File.ReadAllBytes(marker);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }

        if (!content.AsSpan(0, 4).SequenceEqual("RGBX"u8))
            yield break;
        foreach (var folder in Encoding.Unicode.GetString(content, 8, content.Length - 8)
                     .Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(root, folder.TrimStart('\\', '/'));
            if (ExistingDirectory(candidate) is { } path && SavePaths.IsWithin(path, root) &&
                !path.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    internal static InstalledGame? ReadXboxGame(string directory, string localAppData)
    {
        var content = Path.Combine(directory, "Content");
        if (!File.Exists(Path.Combine(content, "MicrosoftGame.config")))
            content = directory;
        try
        {
            using var input = File.OpenRead(Path.Combine(content, "MicrosoftGame.config"));
            var game = XDocument.Load(input).Root;
            var identity = game?.Element("Identity")?.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(identity))
                return null;
            var visuals = game?.Element("ShellVisuals");
            var name = visuals?.Attribute("DefaultDisplayName")?.Value;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileName(directory);

            var icon = ExistingGameFile(content, visuals?.Attribute("Square44x44Logo")?.Value);
            var exe = game?.Element("ExecutableList")?.Elements("Executable")
                .Select(item => ExistingGameFile(content, item.Attribute("Name")?.Value)).FirstOrDefault(File.Exists);
            var packages = Path.Combine(localAppData, "Packages");
            var saves = SavePaths.Enumerate(packages, "*", directories: true)
                .Where(package => Path.GetFileName(package).StartsWith(identity + "_", StringComparison.OrdinalIgnoreCase))
                .Select(package => Path.Combine(package, "SystemAppData", "wgs")).Where(Directory.Exists).ToArray();
            return new InstalledGame(name, "microsoft", identity, content, icon ?? exe, null, saves);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    private static string? UbisoftRoot() =>
        ExistingDirectory(ReadRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir")) ??
        ExistingDirectory(ReadRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Launcher", "InstallDir")) ??
        ExistingDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Ubisoft", "Ubisoft Game Launcher"));

    private static string? ExistingDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        var path = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
        return Path.IsPathFullyQualified(path) && Directory.Exists(path)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
    }

    private static string? ExistingGameFile(string directory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;
        var file = Path.Combine(directory, relativePath);
        return File.Exists(file) && SavePaths.IsWithin(file, directory) ? file : null;
    }

    private static string? ReadDisplayIcon(string? value)
    {
        if (value is null)
            return null;
        var comma = value.LastIndexOf(',');
        var path = Environment.ExpandEnvironmentVariables(
            (comma >= 0 && int.TryParse(value[(comma + 1)..], out _) ? value[..comma] : value).Trim().Trim('"'));
        return Path.IsPathFullyQualified(path) && File.Exists(path) &&
            (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) ? path : null;
    }

    private static string? TextValue(RegistryKey? key, params string[] names) =>
        names.Select(name => key?.GetValue(name) as string).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
