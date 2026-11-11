using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SaveDock;

internal static partial class RegistryData
{
    public static bool Exists(string path)
    {
        try
        {
            if (!TrySplit(path, out var root, out var key))
                return false;
            using var found = root.OpenSubKey(key);
            return found is not null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    public static RegistryKeyData[] Capture(IEnumerable<string> paths)
    {
        var keys = new SortedDictionary<string, RegistryKeyData>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!TrySplit(path, out var root, out var subkey))
                throw new IOException(L.T($"Unsupported registry path: {path}", $"Nicht unterstützter Registry-Pfad: {path}"));
            using var key = root.OpenSubKey(subkey);
            if (key is not null)
                CaptureKey(key, keys);
        }
        return keys.Values.ToArray();
    }

    private static void CaptureKey(RegistryKey key, SortedDictionary<string, RegistryKeyData> keys)
    {
        if (keys.ContainsKey(key.Name))
            return;

        var values = new List<RegistryValueData>();
        foreach (var name in key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (data is not null)
                values.Add(new RegistryValueData(name, key.GetValueKind(name), Encode(data)));
        }
        keys.Add(key.Name, new RegistryKeyData(key.Name, values.ToArray()));

        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is not null)
                CaptureKey(child, keys);
        }
    }

    private static string Encode(object value) => value switch
    {
        string text => text,
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        string[] strings => JsonSerializer.Serialize(strings),
        _ => throw new IOException(L.T("A registry value has an unknown data type.", "Ein Registry-Wert hat einen unbekannten Datentyp."))
    };

    private static object Decode(RegistryValueData value) => value.Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => value.Data,
        RegistryValueKind.DWord => int.Parse(value.Data, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(value.Data, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(value.Data),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value.Data) ?? [],
        _ => throw new IOException(L.T($"Unsupported registry value type: {value.Kind}", $"Nicht unterstützter Registry-Datentyp: {value.Kind}"))
    };

    public static RegistryBeforeLoad Apply(IEnumerable<RegistryKeyData> keys, Stack<Action> rollback)
    {
        var values = new List<RegistryBeforeValue>();
        var newKeys = new List<string>();
        foreach (var entry in keys)
        {
            if (!TrySplit(entry.Path, out var root, out var path))
                throw new IOException(L.T($"Unsupported registry path: {entry.Path}", $"Nicht unterstützter Registry-Pfad: {entry.Path}"));

            using var key = OpenOrCreate(root, path, rollback, newKeys);

            foreach (var value in entry.Values)
            {
                var previous = ReadCurrent(key, value.Name);
                ChangeValue(key, value.Name, value);
                RegisterValueRollback(rollback, root, path, value.Name, previous);
                values.Add(new RegistryBeforeValue(key.Name, value.Name, previous));
            }
        }
        return new RegistryBeforeLoad(values.ToArray(), newKeys.ToArray());
    }

    public static void ApplyBeforeLoad(RegistryBeforeLoad state, Stack<Action> rollback)
    {
        foreach (var value in state.Values.Reverse())
        {
            if (!TrySplit(value.Path, out var root, out var path))
                throw new IOException(L.T($"Unsupported registry path: {value.Path}", $"Nicht unterstützter Registry-Pfad: {value.Path}"));

            using var key = value.Previous is null ? root.OpenSubKey(path, writable: true) : OpenOrCreate(root, path, rollback);
            if (key is null)
                continue;

            var current = ReadCurrent(key, value.Name);
            ChangeValue(key, value.Name, value.Previous);
            RegisterValueRollback(rollback, root, path, value.Name, current);
        }

        foreach (var created in state.CreatedKeys.Reverse())
        {
            if (!TrySplit(created, out var root, out var path))
                throw new IOException(L.T($"Unsupported registry path: {created}", $"Nicht unterstützter Registry-Pfad: {created}"));
            if (RemoveEmptyKey(root, path))
                rollback.Push(() => root.CreateSubKey(path, writable: true)?.Dispose());
        }
    }

    private static RegistryKey OpenOrCreate(RegistryKey root, string path, Stack<Action> rollback, List<string>? newKeys = null)
    {
        var missing = new Stack<string>();
        for (var candidate = path; candidate.StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase);
             candidate = candidate[..candidate.LastIndexOf('\\')])
        {
            using var found = root.OpenSubKey(candidate);
            if (found is not null)
                break;
            missing.Push(candidate);
        }

        // Register the possible newly created parents before touching the registry.
        foreach (var created in missing)
        {
            rollback.Push(() => RemoveEmptyKey(root, created));
            newKeys?.Add(root.Name + "\\" + created);
        }
        return root.CreateSubKey(path, writable: true)
            ?? throw new IOException(L.T($"Could not open registry key: {root.Name}\\{path}", $"Registry-Schlüssel konnte nicht geöffnet werden: {root.Name}\\{path}"));
    }

    private static bool RemoveEmptyKey(RegistryKey root, string path)
    {
        using (var key = root.OpenSubKey(path))
            if (key is not { ValueCount: 0, SubKeyCount: 0 })
                return false;

        root.DeleteSubKey(path, throwOnMissingSubKey: false);
        return true;
    }

    private static RegistryValueData? ReadCurrent(RegistryKey key, string name)
    {
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is not null)
            return new RegistryValueData(name, key.GetValueKind(name), Encode(raw));
        if (key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new IOException(L.T($"Could not back up an existing registry value: {key.Name}\\{name}", $"Ein vorhandener Registry-Wert kann nicht gesichert werden: {key.Name}\\{name}"));
        return null;
    }

    private static void ChangeValue(RegistryKey key, string name, RegistryValueData? value)
    {
        if (value is null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, Decode(value), value.Kind);
    }

    private static void RegisterValueRollback(Stack<Action> rollback, RegistryKey root, string path, string name,
        RegistryValueData? previous) => rollback.Push(() =>
    {
        using var key = root.OpenSubKey(path, writable: true)
            ?? throw new IOException(L.T($"Registry key is missing: {root.Name}\\{path}", $"Registry-Schlüssel fehlt: {root.Name}\\{path}"));
        ChangeValue(key, name, previous);
    });

    public static void ValidateBeforeLoad(RegistryBeforeLoad state)
    {
        foreach (var path in state.Values.Select(value => value.Path).Concat(state.CreatedKeys))
            if (!TrySplit(path, out _, out _))
                throw new IOException(L.T($"Unsupported registry path: {path}", $"Nicht unterstützter Registry-Pfad: {path}"));
        foreach (var value in state.Values)
            if (value.Previous is not null)
                _ = Decode(value.Previous);
    }

    public static void Validate(IEnumerable<RegistryKeyData> keys)
    {
        foreach (var key in keys)
        {
            if (!TrySplit(key.Path, out _, out _))
                throw new IOException(L.T($"Unsupported registry path: {key.Path}", $"Nicht unterstützter Registry-Pfad: {key.Path}"));
            foreach (var value in key.Values)
                _ = Decode(value);
        }
    }

    public static RegistryKeyData[] ReadReg(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var keys = new List<RegistryKeyData>();
        var values = new List<RegistryValueData>();
        string? key = null;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("Windows Registry Editor") || line == "REGEDIT4")
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (key is not null)
                    keys.Add(new RegistryKeyData(key, values.ToArray()));
                key = line[1..^1];
                values.Clear();
                continue;
            }

            while (line.EndsWith('\\'))
            {
                var next = reader.ReadLine() ?? throw new IOException(L.T("An incomplete line was found in registry.reg.", "Unvollständige Zeile in registry.reg."));
                line = line[..^1] + next.Trim();
            }

            var match = RegValue().Match(line);
            if (key is null || !match.Success)
                throw new IOException(L.T("An entry in registry.reg could not be read.", "Ein Eintrag in registry.reg konnte nicht gelesen werden."));
            var name = match.Groups[2].Success ? Unescape(match.Groups[2].Value) : "";
            values.Add(ReadValue(name, match.Groups[3].Value));
        }

        if (key is not null)
            keys.Add(new RegistryKeyData(key, values.ToArray()));
        return keys.ToArray();
    }

    private static RegistryValueData ReadValue(string name, string value)
    {
        if (value.StartsWith('"') && value.EndsWith('"'))
            return new RegistryValueData(name, RegistryValueKind.String, Unescape(value[1..^1]));
        if (value.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            var number = uint.Parse(value[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new RegistryValueData(name, RegistryValueKind.DWord, Encode(unchecked((int)number)));
        }

        var colon = value.IndexOf(':');
        if (colon < 0 || !value.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T($"Unsupported registry value: {name}", $"Nicht unterstützter Registry-Wert: {name}"));

        var type = value[..colon].ToLowerInvariant();
        var bytes = Convert.FromHexString(value[(colon + 1)..].Replace(",", "").Replace(" ", "").Replace("\t", ""));
        var (kind, decoded) = type switch
        {
            "hex" or "hex(3)" => (RegistryValueKind.Binary, (object)bytes),
            "hex(0)" => (RegistryValueKind.None, bytes),
            "hex(1)" => (RegistryValueKind.String, Encoding.Unicode.GetString(bytes).TrimEnd('\0')),
            "hex(2)" => (RegistryValueKind.ExpandString, Encoding.Unicode.GetString(bytes).TrimEnd('\0')),
            "hex(4)" => (RegistryValueKind.DWord, BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            "hex(5)" => (RegistryValueKind.DWord, BinaryPrimitives.ReadInt32BigEndian(bytes)),
            "hex(7)" => (RegistryValueKind.MultiString, Encoding.Unicode.GetString(bytes).TrimEnd('\0')
                .Split('\0', StringSplitOptions.None)),
            "hex(b)" => (RegistryValueKind.QWord, BinaryPrimitives.ReadInt64LittleEndian(bytes)),
            _ => throw new IOException(L.T($"Unsupported registry value type: {type}", $"Nicht unterstützter Registry-Datentyp: {type}"))
        };
        return new RegistryValueData(name, kind, Encode(decoded));
    }

    private static string Unescape(string text) => text.Replace("\\\"", "\"").Replace(@"\\", @"\");

    private static bool TrySplit(string path, out RegistryKey root, out string key)
    {
        var canonical = path.Replace('/', '\\').TrimEnd('\\');
        var separator = canonical.IndexOf('\\');
        key = separator < 0 ? "" : canonical[(separator + 1)..];
        root = (separator < 0 ? canonical : canonical[..separator]).ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            _ => null!
        };
        return root is not null && key.StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase)
            && key.Length > "Software\\".Length;
    }

    [GeneratedRegex("""^(@|"((?:\\.|[^"\\])*)")\s*=\s*(.+)$""")]
    private static partial Regex RegValue();
}

internal sealed record RegistryKeyData(string Path, RegistryValueData[] Values);
internal sealed record RegistryValueData(string Name, RegistryValueKind Kind, string Data);
internal sealed record RegistryBeforeValue(string Path, string Name, RegistryValueData? Previous);
internal sealed record RegistryBeforeLoad(RegistryBeforeValue[] Values, string[] CreatedKeys)
{
    public static RegistryBeforeLoad Empty => new([], []);
}
