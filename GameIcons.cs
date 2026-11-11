using Avalonia.Media.Imaging;

namespace SaveDock;

internal static class GameIcons
{
    public static Bitmap? Read(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var icon = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    ? System.Drawing.Icon.ExtractAssociatedIcon(path)
                    : new System.Drawing.Icon(path);
                if (icon is null)
                    return null;
                using var converted = icon.ToBitmap();
                using var stream = new MemoryStream();
                converted.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                return new Bitmap(stream);
            }

            using var file = File.OpenRead(path);
            return Bitmap.DecodeToWidth(file, 64);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or
            InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }
}
