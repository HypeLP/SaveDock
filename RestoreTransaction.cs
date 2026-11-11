using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SaveDock;

internal static class RestoreTransaction
{
    public static void Run(RestorePlan plan)
    {
        if (plan.Files.Length == 0 && plan.Registry is null)
            throw new IOException("Diese Sicherung enthält keine wiederherstellbaren Spielstände.");

        var archives = new Dictionary<string, ZipArchive>(StringComparer.OrdinalIgnoreCase);
        var staged = new List<StagedFile>();
        var undo = new Stack<Action>();
        var hashAlgorithm = plan.Version.Legacy ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;
        try
        {
            var registry = ReadRegistry(plan, archives, hashAlgorithm);
            RegistryData.Validate(registry);

            foreach (var file in plan.Files)
            {
                var folder = Path.GetDirectoryName(file.Target)!;
                Directory.CreateDirectory(folder);
                var temporary = Path.Combine(folder, $".savedock-{Guid.NewGuid():N}.tmp");
                staged.Add(new StagedFile(file.Target, temporary));
                using (var destination = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var entry = GetEntry(archives, file.Payload);
                    using var source = entry.Open();
                    CopyVerified(file.Payload, source, destination, hashAlgorithm);
                }
                if (file.When is DateTime when)
                    File.SetLastWriteTimeUtc(temporary, when);
            }

            foreach (var file in staged)
            {
                if (File.Exists(file.Target))
                {
                    var original = file.Temporary + ".original";
                    File.Replace(file.Temporary, file.Target, original);
                    file.Original = original;
                    undo.Push(() => File.Move(original, file.Target, overwrite: true));
                }
                else
                {
                    File.Move(file.Temporary, file.Target);
                    undo.Push(() => File.Delete(file.Target));
                }
            }

            RegistryData.Apply(registry, undo);
            foreach (var file in staged)
                if (file.Original is not null)
                    BackupStore.DeleteTemporary(file.Original);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<string>();
            while (undo.TryPop(out var action))
            {
                try { action(); }
                catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
            }
            if (rollbackErrors.Count > 0)
                throw new IOException("Die Wiederherstellung wurde unterbrochen. Einige Änderungen konnten nicht rückgängig gemacht werden: " +
                    string.Join(" · ", rollbackErrors), error);
            throw;
        }
        finally
        {
            foreach (var file in staged)
                BackupStore.DeleteTemporary(file.Temporary);
            foreach (var archive in archives.Values)
                archive.Dispose();
        }
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
            throw new IOException($"In {Path.GetFileName(payload.Archive)} fehlt {payload.Entry}.");
    }

    private static void CopyVerified(BackupPayload payload, Stream source, Stream destination, HashAlgorithmName algorithm)
    {
        var hash = BackupStore.CopyAndHash(source, destination, algorithm);
        if (payload.Size is long size && destination.Length != size ||
            !string.IsNullOrEmpty(payload.Hash) && !hash.Equals(payload.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Die Prüfsumme der Sicherung stimmt nicht: {payload.Entry}");
    }

    private sealed record StagedFile(string Target, string Temporary)
    {
        public string? Original { get; set; }
    }
}
