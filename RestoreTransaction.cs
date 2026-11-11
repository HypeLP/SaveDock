using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SaveDock;

internal static class RestoreTransaction
{
    public static void Run(RestorePlan plan, UndoStore? undoStore = null)
    {
        if (plan.Files.Length == 0 && plan.Registry is null)
            throw new IOException(L.T("This backup does not contain any restorable game saves.", "Diese Sicherung enthält keine wiederherstellbaren Spielstände."));

        var archives = new Dictionary<string, ZipArchive>(StringComparer.OrdinalIgnoreCase);
        var hashAlgorithm = plan.Version.Legacy ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;
        try
        {
            var registry = ReadRegistry(plan, archives, hashAlgorithm);
            RegistryData.Validate(registry);
            var registryBefore = RegistryBeforeLoad.Empty;
            Execute(plan.Files.Select(file => new FileChange(file.Target, file.When, file.Payload)), archives,
                hashAlgorithm, rollback => registryBefore = RegistryData.Apply(registry, rollback),
                files => undoStore?.Save(plan.Version, files, registryBefore));
        }
        finally { CloseArchives(archives); }
    }

    public static void Revert(UndoSnapshot snapshot, string archive, Action consume)
    {
        RegistryData.ValidateBeforeLoad(snapshot.Registry);
        var files = snapshot.Files.Select(file =>
        {
            if (file.Entry is not null && (string.IsNullOrEmpty(file.Hash) || file.Size is null))
                throw new IOException(L.T($"The undo snapshot is incomplete: {file.Target}", $"Der Rückgängig-Stand ist unvollständig: {file.Target}"));
            return new FileChange(file.Target, file.When, file.Entry is null ? null :
                new BackupPayload(archive, file.Entry, file.Hash, file.Size));
        });
        Execute(files, new Dictionary<string, ZipArchive>(StringComparer.OrdinalIgnoreCase), HashAlgorithmName.SHA256,
            rollback => RegistryData.ApplyBeforeLoad(snapshot.Registry, rollback), _ => consume());
    }

    private static void Execute(IEnumerable<FileChange> changes, Dictionary<string, ZipArchive> archives,
        HashAlgorithmName algorithm, Action<Stack<Action>> applyRegistry, Action<IEnumerable<BeforeLoadFile>> commit)
    {
        var staged = new List<StagedFile>();
        var rollback = new Stack<Action>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in changes)
            {
                var target = StoredFile.ValidateTarget(file.Target);
                if (!targets.Add(target))
                    throw new IOException(L.T($"Duplicate destination path: {target}", $"Mehrfacher Zielpfad: {target}"));

                var directory = Path.GetDirectoryName(target)!;
                var temporary = Path.Combine(directory, $".savedock-{Guid.NewGuid():N}.tmp");
                staged.Add(new StagedFile(target, temporary, Remove: file.Payload is null));
                if (file.Payload is not { } payload)
                    continue;

                Directory.CreateDirectory(directory);
                using (var destination = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var entry = GetEntry(archives, payload);
                    using var source = entry.Open();
                    CopyVerified(payload, source, destination, algorithm);
                }
                if (file.When is DateTime when)
                    File.SetLastWriteTimeUtc(temporary, when);
            }

            CloseArchives(archives);
            foreach (var file in staged)
                ApplyFile(file, rollback);
            applyRegistry(rollback);
            commit(staged.Select(file => new BeforeLoadFile(file.Target, file.Original)));

            foreach (var file in staged)
                if (file.Original is not null)
                    BackupStore.DeleteTemporary(file.Original);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<string>();
            while (rollback.TryPop(out var action))
            {
                try { action(); }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError.Message); }
            }
            if (rollbackErrors.Count > 0)
                throw new IOException(L.T("The restore was interrupted. Some changes could not be rolled back: ", "Die Wiederherstellung wurde unterbrochen. Einige Änderungen konnten nicht rückgängig gemacht werden: ") +
                    string.Join(" · ", rollbackErrors), error);
            throw;
        }
        finally
        {
            foreach (var file in staged)
                BackupStore.DeleteTemporary(file.Temporary);
            CloseArchives(archives);
        }
    }

    private static void ApplyFile(StagedFile file, Stack<Action> rollback)
    {
        if (Exists(file.Target))
        {
            var original = file.Temporary + ".original";
            if (file.Remove)
                File.Move(file.Target, original);
            else
                File.Replace(file.Temporary, file.Target, original);
            file.Original = original;
            rollback.Push(() => File.Move(original, file.Target, overwrite: true));
        }
        else if (!file.Remove)
        {
            File.Move(file.Temporary, file.Target);
            rollback.Push(() => File.Delete(file.Target));
        }
    }

    private static bool Exists(string target)
    {
        try
        {
            if (File.GetAttributes(target).HasFlag(FileAttributes.Directory))
                throw new IOException(L.T($"A folder occupies the destination path: {target}", $"Am Zielpfad liegt ein Ordner: {target}"));
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static void CloseArchives(Dictionary<string, ZipArchive> archives)
    {
        foreach (var archive in archives.Values)
            archive.Dispose();
        archives.Clear();
    }

    private static RegistryKeyData[] ReadRegistry(RestorePlan plan, Dictionary<string, ZipArchive> archives,
        HashAlgorithmName algorithm)
    {
        if (plan.Registry is not { } registry)
            return [];

        using var entry = GetEntry(archives, registry).Open();
        using var contents = new MemoryStream();
        CopyVerified(registry, entry, contents, algorithm);
        contents.Position = 0;
        return plan.Version.Legacy
            ? RegistryData.ReadReg(contents)
            : JsonSerializer.Deserialize<RegistryKeyData[]>(contents) ?? [];
    }

    private static ZipArchiveEntry GetEntry(Dictionary<string, ZipArchive> archives, BackupPayload payload)
    {
        if (!archives.TryGetValue(payload.Archive, out var zip))
        {
            zip = ZipFile.OpenRead(payload.Archive);
            archives.Add(payload.Archive, zip);
        }
        return zip.GetEntry(payload.Entry) ??
            throw new IOException(L.T($"{payload.Entry} is missing from {Path.GetFileName(payload.Archive)}.", $"In {Path.GetFileName(payload.Archive)} fehlt {payload.Entry}."));
    }

    private static void CopyVerified(BackupPayload payload, Stream source, Stream destination, HashAlgorithmName algorithm)
    {
        var hash = BackupStore.CopyAndHash(source, destination, algorithm);
        if (payload.Size is long size && destination.Length != size ||
            !string.IsNullOrEmpty(payload.Hash) && !hash.Equals(payload.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T($"Backup checksum mismatch: {payload.Entry}", $"Die Prüfsumme der Sicherung stimmt nicht: {payload.Entry}"));
    }

    private sealed record FileChange(string Target, DateTime? When, BackupPayload? Payload);

    private sealed record StagedFile(string Target, string Temporary, bool Remove)
    {
        public string? Original { get; set; }
    }
}
