using System.Text.Json;

namespace SaveDock;

internal sealed record CustomGame(string Name, string[] Paths, string? IconPath);

internal sealed record UserSettings(string? Destination, CustomGame[]? CustomGames = null, BackupOptions? Backup = null)
{
    public BackupOptions BackupPreferences() => (Backup ?? new BackupOptions()).Normalize();

    public static UserSettings Load(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path))
            return new UserSettings(Destination: null);

        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path));
        if (settings is null)
            return new UserSettings(Destination: null);

        return settings.Destination is string destination && !Path.IsPathFullyQualified(destination)
            ? settings with { Destination = null }
            : settings;
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
