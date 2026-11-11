using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SaveDock;

internal sealed class BackupStore(string target)
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private string NativeRoot => Path.Combine(target, "SaveDock");

    public BackupHistory ReadHistory()
    {
        var versions = new List<BackupVersion>();
        var warnings = new List<string>();
        foreach (var directory in SavePaths.Enumerate(NativeRoot, "*", directories: true))
        {
            try
            {
                var index = ReadIndex(directory);
                if (index is not null)
                    versions.AddRange(index.Snapshots.Where(item => !item.Hidden).Select(item =>
                        new BackupVersion(index.Game, directory, item.Name, item.When, item.Base is not null, false)));
            }
            catch (Exception error)
            {
                warnings.Add($"{Path.GetFileName(directory)}: {error.Message}");
            }
        }

        foreach (var directory in SavePaths.Enumerate(target, "*", directories: true))
        {
            var mapping = Path.Combine(directory, "mapping.yaml");
            if (!File.Exists(mapping))
                continue;
            try { versions.AddRange(LegacyBackup.ReadVersions(directory)); }
            catch (Exception error) { warnings.Add($"{Path.GetFileName(directory)}: {error.Message}"); }
        }

        var groups = versions.GroupBy(version => version.Game, PathComparer).ToDictionary(group => group.Key,
            group => group.OrderByDescending(version => version.When).ToArray(), PathComparer);
        return new BackupHistory(groups, warnings);
    }

    public bool Save(GameSave game, BackupOptions? preferences = null)
    {
        if (game.Entries.Count == 0)
            return false;

        using var targetLock = OpenLock(create: true);
        var directory = Path.Combine(NativeRoot, GameFolder(game.Name));
        Directory.CreateDirectory(directory);
        var index = ReadIndex(directory) ?? new BackupIndex(game.Name, []);
        var options = (preferences ?? new BackupOptions()).Normalize();
        var latest = index.Snapshots.LastOrDefault(snapshot => !snapshot.Hidden);
        var latestIntact = latest is not null && Exists(directory, latest.Name) &&
            (latest.Base is null || index.Snapshots.Any(item => item.Name == latest.Base) && Exists(directory, latest.Base));
        var forceFull = options.Mode == BackupMode.Full && options.Retention != RetentionMode.Latest;
        var replaceDifferential = options.Retention == RetentionMode.Latest && latest?.Base is not null;
        var quickCheck = options.Check == FileCheckMode.Metadata && !forceFull && !replaceDifferential && latestIntact;
        var known = quickCheck ? latest!.Files.ToDictionary(file => file.Path, PathComparer) : null;
        var exclusion = new PathBoundary(target);
        var entries = game.Entries.Where(entry => !entry.IsRegistry).Select(entry =>
        {
            if (exclusion.Contains(entry.Path))
                throw new IOException($"Der Spielstand befindet sich im Backup-Ordner: {entry.Path}");
            return StoredFile.From(entry.Path, known?.GetValueOrDefault(entry.Path));
        }).OrderBy(entry => entry.Path, PathComparer).ToArray();
        var registry = RegistryData.Capture(game.Entries.Where(entry => entry.IsRegistry).Select(entry => entry.Path));
        if (entries.Length == 0 && registry.Length == 0)
            throw new IOException("Für dieses Spiel wurden keine lesbaren Spielstände gefunden.");

        var registryBytes = registry.Length == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(registry);
        var registryHash = registryBytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(registryBytes));
        if (!forceFull && !replaceDifferential && latestIntact && latest!.RegistryHash == registryHash &&
            latest.Files.Select(file => (file.Path, file.Hash)).SequenceEqual(entries.Select(file => (file.Path, file.Hash))))
        {
            var refreshed = latest.Files.Zip(entries, (stored, current) =>
                stored.When == current.When && stored.CreatedUtc != current.CreatedUtc
                    ? stored with { CreatedUtc = current.CreatedUtc } : stored).ToArray();
            var metadataChanged = !latest.Files.SequenceEqual(refreshed);
            if (metadataChanged)
                index.Snapshots[index.Snapshots.IndexOf(latest)] = latest with { Files = refreshed };
            ApplyRetention(directory, index, options, metadataChanged);
            return false;
        }

        var baseline = forceFull || options.Retention == RetentionMode.Latest ? null :
            index.Snapshots.LastOrDefault(snapshot => snapshot.Base is null && Exists(directory, snapshot.Name));
        var differentialCount = baseline is null ? 0 : Math.Max(baseline.DifferentialsCreated,
            index.Snapshots.Count(snapshot => snapshot.Base == baseline.Name));
        if (options.Mode == BackupMode.Automatic && baseline is not null &&
            differentialCount >= 5)
            baseline = null;
        var when = DateTimeOffset.UtcNow;
        var name = $"backup-{when:yyyyMMdd'T'HHmmssfffffff'Z'}{(baseline is null ? "" : "-diff")}.zip";
        var version = new Snapshot(name, when, baseline?.Name, entries, registryHash);
        var baseHashes = baseline?.Files.Select(file => file.Hash).ToHashSet(StringComparer.Ordinal) ?? [];
        var archive = ArchivePath(directory, name);
        var temporary = archive + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in entries.DistinctBy(file => file.Hash).Where(file => !baseHashes.Contains(file.Hash)))
                {
                    using var source = StoredFile.OpenSource(file.Path);
                    using var destination = zip.CreateEntry(FileKey(file.Hash), options.ZipLevel).Open();
                    if (CopyAndHash(source, destination, HashAlgorithmName.SHA256) != file.Hash)
                        throw new IOException($"Der Spielstand wurde während der Sicherung geändert: {file.Path}");
                }
                if (registryBytes is not null && baseline?.RegistryHash != registryHash)
                {
                    using var destination = zip.CreateEntry("registry.json", options.ZipLevel).Open();
                    destination.Write(registryBytes);
                }
            }

            File.Move(temporary, archive);
            if (baseline is not null)
                index.Snapshots[index.Snapshots.IndexOf(baseline)] = baseline with { DifferentialsCreated = differentialCount + 1 };
            index.Snapshots.Add(version);
            try { ApplyRetention(directory, index, options, indexChanged: true); }
            catch
            {
                DeleteTemporary(archive);
                throw;
            }
            return true;
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    private static bool Exists(string directory, string archive) => File.Exists(ArchivePath(directory, archive));

    private static void ApplyRetention(string directory, BackupIndex index, BackupOptions options, bool indexChanged)
    {
        var snapshots = index.Snapshots;
        HashSet<string> keep;
        HashSet<string>? visible = null;
        if (options.Retention == RetentionMode.Standard)
        {
            var bases = snapshots.Where(item => item.Base is null).TakeLast(3).ToArray();
            keep = bases.Select(item => item.Name).Concat(bases.SelectMany(baseline =>
                snapshots.Where(item => item.Base == baseline.Name).TakeLast(5).Select(item => item.Name))).ToHashSet();
        }
        else
        {
            var count = options.Retention == RetentionMode.Latest ? 1 : options.KeepVersions;
            var recent = snapshots.Where(item => !item.Hidden).TakeLast(count).ToArray();
            visible = recent.Select(item => item.Name).ToHashSet();
            keep = visible.Concat(recent.Select(item => item.Base).OfType<string>()).ToHashSet();
        }

        var expired = new List<string>();
        for (var i = snapshots.Count - 1; i >= 0; i--)
        {
            var item = snapshots[i];
            if (!keep.Contains(item.Name))
            {
                expired.Add(ArchivePath(directory, item.Name));
                snapshots.RemoveAt(i);
            }
            else if (visible is not null && !visible.Contains(item.Name) && !item.Hidden)
            {
                snapshots[i] = item with { Hidden = true };
                indexChanged = true;
            }
        }

        if (indexChanged || expired.Count > 0)
            WriteIndex(directory, index);
        foreach (var archive in expired)
            DeleteTemporary(archive);
    }

    public RestorePlan Preview(BackupVersion version)
    {
        var plan = version.Legacy ? LegacyBackup.Preview(version) : NativePreview(version);
        foreach (var file in plan.Files)
            if (SavePaths.IsWithin(file.Target, target))
                throw new IOException($"Ein Zielpfad liegt im Backup-Ordner: {file.Target}");
        return plan;
    }

    public void Restore(BackupVersion version)
    {
        using var targetLock = OpenLock(create: false);
        RestoreTransaction.Run(Preview(version));
    }

    private RestorePlan NativePreview(BackupVersion version)
    {
        var index = ReadIndex(version.Directory) ?? throw new IOException("Die Sicherung wurde verschoben.");
        var selected = index.Snapshots.SingleOrDefault(item => item.Name == version.Name)
            ?? throw new IOException("Die Sicherung ist nicht mehr vorhanden.");
        var baseline = selected.Base is null ? null : index.Snapshots.SingleOrDefault(item => item.Name == selected.Base)
            ?? throw new IOException("Die zugehörige Vollsicherung fehlt.");
        if (!Exists(version.Directory, selected.Name) || baseline is not null && !Exists(version.Directory, baseline.Name))
            throw new IOException("Ein ZIP-Archiv dieser Sicherung fehlt.");
        var baseHashes = baseline?.Files.Select(file => file.Hash).ToHashSet(StringComparer.Ordinal) ?? [];
        var archive = ArchivePath(version.Directory, selected.Name);
        var files = selected.Files.Select(file => new RestoreFile(file.GetTarget(), file.When,
            new BackupPayload(baseHashes.Contains(file.Hash) ? ArchivePath(version.Directory, baseline!.Name) : archive,
                FileKey(file.Hash), file.Hash, file.Size))).ToArray();
        var registry = selected.RegistryHash is null ? null : new BackupPayload(
            selected.RegistryHash == baseline?.RegistryHash ? ArchivePath(version.Directory, baseline.Name) : archive,
            "registry.json", selected.RegistryHash, null);
        return new RestorePlan(version, files, registry);
    }

    private FileStream OpenLock(bool create)
    {
        if (!Directory.Exists(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target))))
            throw new IOException("Der ausgewählte Speicherort ist nicht erreichbar.");
        if (create)
            Directory.CreateDirectory(target);
        return File.Open(Path.Combine(target, ".savedock.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal static string ArchivePath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException($"Ungültiger Archivname: {name}");
        return Path.Combine(directory, name);
    }

    private static string GameFolder(string name)
    {
        var slug = string.Concat(name.Take(60).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim(' ', '.');
        var id = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToUpperInvariant())))[..12];
        return $"{slug}-{id}";
    }

    private static BackupIndex? ReadIndex(string directory)
    {
        var path = Path.Combine(directory, "index.json");
        if (!File.Exists(path))
            return null;
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<BackupIndex>(file, JsonSerializerOptions.Web)
            ?? throw new IOException("Der Sicherungsindex ist leer.");
    }

    private static void WriteIndex(string directory, BackupIndex index)
    {
        var path = Path.Combine(directory, "index.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, index, JsonSerializerOptions.Web);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { DeleteTemporary(temporary); }
    }

    internal static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string FileKey(string hash) => $"files/{hash}.bin";

    internal static string CopyAndHash(Stream source, Stream destination, HashAlgorithmName algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (source.Read(buffer, 0, buffer.Length) is var count && count > 0)
            {
                hash.AppendData(buffer, 0, count);
                destination.Write(buffer, 0, count);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
}

internal sealed record BackupIndex(string Game, List<Snapshot> Snapshots);
internal sealed record Snapshot(string Name, DateTimeOffset When, string? Base, StoredFile[] Files, string? RegistryHash,
    bool Hidden = false, int DifferentialsCreated = 0);
internal sealed record BackupHistory(Dictionary<string, BackupVersion[]> Games, IReadOnlyList<string> Warnings)
{
    public static BackupHistory Empty => new(new(StringComparer.OrdinalIgnoreCase), []);
}

internal sealed record BackupVersion(string Game, string Directory, string Name, DateTimeOffset When,
    bool Differential, bool Legacy)
{
    public override string ToString() =>
        $"{When.ToLocalTime():dd.MM.yyyy HH:mm:ss}  ·  {(Differential ? "Differenzsicherung" : "Vollsicherung")}";
}

internal sealed record RestorePlan(BackupVersion Version, RestoreFile[] Files, BackupPayload? Registry);
internal sealed record RestoreFile(string Target, DateTime? When, BackupPayload Payload);
internal sealed record BackupPayload(string Archive, string Entry, string? Hash, long? Size);

internal sealed record StoredFile(string Path, string Hash, long Size, DateTime When, string? Anchor, string? Relative,
    DateTime? CreatedUtc = null)
{
    public static FileStream OpenSource(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    public static StoredFile From(string path, StoredFile? known = null)
    {
        var info = new FileInfo(path);
        if (known?.CreatedUtc is DateTime created && info.Exists && known.Size == info.Length &&
            known.When == info.LastWriteTimeUtc && created == info.CreationTimeUtc)
            return known;

        using var file = OpenSource(path);
        info.Refresh();
        var root = SavePaths.WindowsFolders.Where(pair => pair.Key is "home" or "winDocuments" or "winAppData" or
                "winLocalAppData" or "winLocalAppDataLow" or "winPublic" or "winProgramData")
            .Where(pair => pair.Value.Length > 0 && SavePaths.IsWithin(path, pair.Value))
            .OrderByDescending(pair => pair.Value.Length).FirstOrDefault();
        return new StoredFile(path, Convert.ToHexStringLower(SHA256.HashData(file)), file.Length,
            info.LastWriteTimeUtc, root.Key,
            root.Key is null ? null : System.IO.Path.GetRelativePath(root.Value, path), info.CreationTimeUtc);
    }

    public string GetTarget()
    {
        if (Anchor is null)
            return ValidateTarget(Path);

        if (!SavePaths.WindowsFolders.TryGetValue(Anchor, out var root) || string.IsNullOrWhiteSpace(root) ||
            Relative is null || System.IO.Path.IsPathRooted(Relative))
            throw new IOException($"Der Zielordner für diesen Spielstand ist nicht verfügbar: {Path}");
        var target = ValidateTarget(System.IO.Path.Combine(root, Relative));
        if (!SavePaths.IsWithin(target, root))
            throw new IOException($"Ungültiger relativer Sicherungspfad: {Relative}");
        return target;
    }

    public static string ValidateTarget(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (!System.IO.Path.IsPathFullyQualified(normalized) || normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new IOException($"Ungültiger Zielpfad: {path}");
        var full = System.IO.Path.GetFullPath(normalized);
        var root = System.IO.Path.GetPathRoot(full)!;
        if (full.Length <= root.Length || full[root.Length..].Contains(':'))
            throw new IOException($"Ungültiger Zielpfad: {path}");
        return full;
    }
}
