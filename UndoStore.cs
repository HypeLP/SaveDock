using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SaveDock;

internal sealed class UndoStore(string dataDirectory)
{
    private string DirectoryPath => Path.Combine(dataDirectory, "Undo");

    public FileStream Lock(string game)
    {
        Directory.CreateDirectory(DirectoryPath);
        return File.Open(Path.ChangeExtension(ArchivePath(game), ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public HashSet<string> AvailableGames()
    {
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in SavePaths.Enumerate(DirectoryPath, "*.zip", directories: false))
        {
            try
            {
                var snapshot = Read(path);
                if (string.Equals(path, ArchivePath(snapshot.Game), StringComparison.OrdinalIgnoreCase))
                    games.Add(snapshot.Game);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                // A broken undo archive does not prevent other games or backups from loading.
            }
        }
        return games;
    }

    public UndoSnapshot Preview(string game)
    {
        var snapshot = Read(ArchivePath(game));
        if (!snapshot.Game.Equals(game, StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("The undo snapshot belongs to a different game.", "Der Rückgängig-Stand gehört zu einem anderen Spiel."));
        return snapshot;
    }

    public void Save(BackupVersion loaded, IEnumerable<BeforeLoadFile> originalFiles, RegistryBeforeLoad registry)
    {
        var archive = ArchivePath(loaded.Game);
        var temporary = archive + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var files = new List<UndoFile>();
                    foreach (var file in originalFiles)
                    {
                        if (file.Original is null)
                        {
                            files.Add(new UndoFile(file.Target, null, null, null, null));
                            continue;
                        }

                        var entryName = $"files/{files.Count:D8}.bin";
                        using var source = StoredFile.OpenSource(file.Original);
                        using var destination = zip.CreateEntry(entryName, CompressionLevel.Fastest).Open();
                        var hash = BackupStore.CopyAndHash(source, destination, HashAlgorithmName.SHA256);
                        files.Add(new UndoFile(file.Target, entryName, hash, source.Length,
                            File.GetLastWriteTimeUtc(file.Original)));
                    }

                    var snapshot = new UndoSnapshot(Guid.NewGuid(), loaded.Game, DateTimeOffset.UtcNow, loaded.When,
                        files.ToArray(), registry);
                    using var metadata = zip.CreateEntry("undo.json", CompressionLevel.Fastest).Open();
                    JsonSerializer.Serialize(metadata, snapshot, JsonSerializerOptions.Web);
                }
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, archive, overwrite: true);
        }
        finally { BackupStore.DeleteTemporary(temporary); }
    }

    public void Revert(string game, Guid operation)
    {
        using var gameLock = Lock(game);
        var archive = ArchivePath(game);
        var snapshot = Preview(game);
        if (snapshot.Id != operation)
            throw new IOException(L.T("The undo snapshot has changed. Open the preview again.", "Der Zustand vor dem Laden hat sich geändert. Öffne die Vorschau erneut."));

        RestoreTransaction.Revert(snapshot, archive, () =>
        {
            var consumed = archive + $".{operation:N}.used";
            File.Move(archive, consumed);
            BackupStore.DeleteTemporary(consumed);
        });
    }

    private string ArchivePath(string game)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(game.ToUpperInvariant())));
        return Path.Combine(DirectoryPath, id + ".zip");
    }

    private static UndoSnapshot Read(string archive)
    {
        using var input = File.Open(archive, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        using var content = zip.GetEntry("undo.json")?.Open()
            ?? throw new IOException(L.T("The undo snapshot is missing its version metadata.", "Der Rückgängig-Stand enthält keine Versionsdaten."));
        var snapshot = JsonSerializer.Deserialize<UndoSnapshot>(content, JsonSerializerOptions.Web);
        if (snapshot is null || snapshot.Id == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Game) ||
            snapshot.Files is null || snapshot.Registry is null ||
            snapshot.Registry.Values is null || snapshot.Registry.CreatedKeys is null)
            throw new IOException(L.T("The undo snapshot is incomplete.", "Der Rückgängig-Stand ist unvollständig."));
        return snapshot;
    }
}

internal sealed record BeforeLoadFile(string Target, string? Original);
internal sealed record UndoFile(string Target, string? Entry, string? Hash, long? Size, DateTime? When);
internal sealed record UndoSnapshot(Guid Id, string Game, DateTimeOffset Created, DateTimeOffset LoadedVersion,
    UndoFile[] Files, RegistryBeforeLoad Registry);
