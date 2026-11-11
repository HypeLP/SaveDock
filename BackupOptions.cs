using System.IO.Compression;

namespace SaveDock;

internal enum BackupMode { Automatic, Full, Differential }
internal enum ZipCompression { Fast, Compact, None }
internal enum RetentionMode { Standard, Latest, Count }
internal enum FileCheckMode { Content, Metadata }

internal sealed record BackupOptions(
    BackupMode Mode = BackupMode.Automatic,
    ZipCompression Compression = ZipCompression.Fast,
    RetentionMode Retention = RetentionMode.Standard,
    int KeepVersions = 10,
    FileCheckMode Check = FileCheckMode.Content)
{
    public const int MaxVersions = 999;

    internal CompressionLevel ZipLevel => Compression switch
    {
        ZipCompression.Compact => CompressionLevel.Optimal,
        ZipCompression.None => CompressionLevel.NoCompression,
        _ => CompressionLevel.Fastest
    };

    public BackupOptions Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : BackupMode.Automatic,
        Compression = Enum.IsDefined(Compression) ? Compression : ZipCompression.Fast,
        Retention = Enum.IsDefined(Retention) ? Retention : RetentionMode.Standard,
        KeepVersions = Math.Clamp(KeepVersions, 2, MaxVersions),
        Check = Enum.IsDefined(Check) ? Check : FileCheckMode.Content
    };
}
