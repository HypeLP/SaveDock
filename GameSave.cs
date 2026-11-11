namespace SaveDock;

internal sealed record GameSave(string Name, IReadOnlyList<SaveEntry> Entries, string? IconPath, CustomGame? Custom,
    string[]? Launchers = null)
{
    public long Bytes => Entries.Sum(entry => entry.Bytes);
}

public sealed record SaveEntry(string Path, long Bytes, bool IsRegistry)
{
    public string Label => IsRegistry ? "Registry" : SizeText.Format(Bytes);
}

internal static class SizeText
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }
}
