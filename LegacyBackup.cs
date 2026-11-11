using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SaveDock;

internal static class LegacyBackup
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();

    public static IEnumerable<BackupVersion> ReadVersions(string directory)
    {
        var mapping = ReadMapping(directory);
        if (string.IsNullOrWhiteSpace(mapping.Name))
            throw new IOException(L.T("The game name is missing from mapping.yaml.", "In mapping.yaml fehlt der Spielname."));
        foreach (var full in mapping.Backups)
        {
            if (IsZip(full.Name))
                yield return Version(mapping.Name, directory, full, differential: false);
            foreach (var diff in full.Children.Where(diff => IsZip(diff.Name)))
                yield return Version(mapping.Name, directory, diff, differential: true);
        }
    }

    public static RestorePlan Preview(BackupVersion version)
    {
        var mapping = ReadMapping(version.Directory);
        var full = mapping.Backups.FirstOrDefault(full => full.Name == version.Name ||
            full.Children.Any(diff => diff.Name == version.Name))
            ?? throw new IOException(L.T("The selected version is missing from mapping.yaml.", "Die gewählte Version fehlt in mapping.yaml."));
        var diff = full.Children.FirstOrDefault(diff => diff.Name == version.Name);
        var records = new Dictionary<string, (LegacyFile File, string Archive)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, file) in full.Files)
            if (file is not null)
                records[path] = (file, full.Name);
        if (diff is not null)
            foreach (var (path, file) in diff.Files)
                if (file is null)
                    records.Remove(path);
                else
                    records[path] = (file, diff.Name);

        var files = records.Select(pair =>
        {
            var target = StoredFile.ValidateTarget(pair.Key);
            var root = Path.GetPathRoot(target)!;
            var drive = mapping.Drives.FirstOrDefault(drive =>
                string.Equals(CanonicalDrive(drive.Value), CanonicalDrive(root), StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(drive.Key))
                throw new IOException(L.T($"The drive mapping for {pair.Key} is missing from mapping.yaml.", $"In mapping.yaml fehlt die Laufwerkszuordnung für {pair.Key}."));
            var entry = $"{drive.Key}/{target[root.Length..].Replace('\\', '/')}";
            var (file, archive) = pair.Value;
            return new RestoreFile(target, null,
                new BackupPayload(BackupStore.ArchivePath(version.Directory, archive), entry, file.Hash, file.Size));
        }).ToArray();

        var registry = diff?.Registry ?? full.Registry;
        var registryFile = diff?.Registry is null ? full.Name : diff.Name;
        var registryPayload = registry?.Hash is null ? null : new BackupPayload(
            BackupStore.ArchivePath(version.Directory, registryFile), "registry.reg", registry.Hash, null);
        return new RestorePlan(version, files, registryPayload);
    }

    private static string CanonicalDrive(string drive) => drive.Replace('/', '\\').TrimEnd('\\');

    private static bool IsZip(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static BackupVersion Version(string game, string directory, LegacySnapshot version, bool differential) =>
        new(game, directory, version.Name, DateTimeOffset.Parse(version.When, CultureInfo.InvariantCulture),
            differential, true);

    private static LegacyMapping ReadMapping(string directory)
    {
        using var source = File.OpenText(Path.Combine(directory, "mapping.yaml"));
        return Yaml.Deserialize<LegacyMapping>(source) ?? throw new IOException(L.T("mapping.yaml is empty.", "mapping.yaml ist leer."));
    }

    private sealed class LegacyMapping
    {
        public string Name { get; set; } = "";
        public Dictionary<string, string> Drives { get; set; } = [];
        public LegacySnapshot[] Backups { get; set; } = [];
    }

    private sealed class LegacySnapshot
    {
        public string Name { get; set; } = "";
        public string When { get; set; } = "";
        public Dictionary<string, LegacyFile?> Files { get; set; } = [];
        public LegacyRegistry? Registry { get; set; }
        public LegacySnapshot[] Children { get; set; } = [];
    }

    private sealed class LegacyFile
    {
        public string? Hash { get; set; }
        public long Size { get; set; }
    }

    private sealed class LegacyRegistry
    {
        public string? Hash { get; set; }
    }
}
