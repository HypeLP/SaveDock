using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace SaveDock;

internal static partial class SavePaths
{
    private static readonly EnumerationOptions TopLevel = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static readonly Dictionary<string, string> WindowsFolders = GetWindowsFolders();

    private static Dictionary<string, string> GetWindowsFolders()
    {
        static string Folder(Environment.SpecialFolder folder) =>
            Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

        var home = Folder(Environment.SpecialFolder.UserProfile);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = home,
            ["osUserName"] = Environment.UserName,
            ["winAppData"] = Folder(Environment.SpecialFolder.ApplicationData),
            ["winLocalAppData"] = Folder(Environment.SpecialFolder.LocalApplicationData),
            ["winLocalAppDataLow"] = Path.Combine(home, "AppData", "LocalLow"),
            ["winDocuments"] = Folder(Environment.SpecialFolder.MyDocuments),
            ["winPublic"] = Environment.GetEnvironmentVariable("PUBLIC") ?? "",
            ["winProgramData"] = Folder(Environment.SpecialFolder.CommonApplicationData),
            ["winDir"] = Folder(Environment.SpecialFolder.Windows)
        };
    }

    public static bool IsWithin(string path, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        return !Path.IsPathFullyQualified(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static IEnumerable<string> Enumerate(string directory, string pattern, bool directories)
    {
        try
        {
            return directories ? Directory.GetDirectories(directory, pattern, TopLevel)
                               : Directory.GetFiles(directory, pattern, TopLevel);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public static IEnumerable<string> FindLiteralFiles(string path, PathBoundary? excluded, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path))
            yield break;
        if (excluded?.Contains(path) == true)
            yield break;
        if (File.Exists(path))
        {
            yield return Path.GetFullPath(path);
            yield break;
        }

        if (Directory.Exists(path))
            foreach (var file in Walk(path, ["**"], 0, excluded, token))
                yield return file;
    }

    public static IEnumerable<string> FindFiles(string path, PathBoundary? excluded, CancellationToken token)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(normalized))
            yield break;

        if (!normalized.Contains('*') && !normalized.Contains('?') &&
            (!normalized.Contains('[') || File.Exists(normalized) || Directory.Exists(normalized)))
        {
            foreach (var file in FindLiteralFiles(normalized, excluded, token))
                yield return file;
            yield break;
        }

        var root = Path.GetPathRoot(normalized)!;
        var segments = normalized[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var file in Walk(root, segments, 0, excluded, token))
            yield return file;
    }

    private static IEnumerable<string> Walk(string path, string[] segments, int index, PathBoundary? excluded,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (excluded?.Contains(path) == true)
            yield break;
        if (index == segments.Length)
        {
            foreach (var file in FindLiteralFiles(path, excluded, token))
                yield return file;
            yield break;
        }

        var segment = segments[index];
        if (segment == "**")
        {
            if (index + 1 < segments.Length)
                foreach (var match in Walk(path, segments, index + 1, excluded, token))
                    yield return match;
            else
                foreach (var file in Enumerate(path, "*", directories: false))
                    yield return file;

            foreach (var folder in Enumerate(path, "*", directories: true))
            foreach (var match in Walk(folder, segments, index, excluded, token))
                yield return match;
            yield break;
        }

        if (!segment.Contains('*') && !segment.Contains('?') && !segment.Contains('['))
        {
            foreach (var match in Walk(Path.Combine(path, segment), segments, index + 1, excluded, token))
                yield return match;
            yield break;
        }

        var candidates = Enumerate(path, "*", directories: true);
        if (index + 1 == segments.Length)
            candidates = candidates.Concat(Enumerate(path, "*", directories: false));
        foreach (var match in candidates.Where(candidate => Matches(segment, Path.GetFileName(candidate))))
        foreach (var file in Walk(match, segments, index + 1, excluded, token))
            yield return file;
    }

    private static bool Matches(string pattern, string name)
    {
        if (!pattern.Contains('['))
            return FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

        var expression = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        expression = CharacterClass().Replace(expression, match => "[" + match.Groups[1].Value + "]");
        return Regex.IsMatch(name, "^" + expression + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"\\\[([a-zA-Z0-9,_!-]+)\]")]
    private static partial Regex CharacterClass();
}

internal sealed class PathBoundary
{
    private readonly string root;
    private readonly string prefix;

    public PathBoundary(string directory)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    }

    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
