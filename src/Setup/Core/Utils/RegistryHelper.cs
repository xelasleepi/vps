using Microsoft.Win32;

namespace Setup.Core.Utils;

/// <summary>
/// Small, exception-safe wrapper over the Windows registry. Shared by the
/// installers (detection) and the optimization tasks (tweaks). All setters
/// create intermediate keys and return a success flag instead of throwing.
/// </summary>
public static class RegistryHelper
{
    private static RegistryKey BaseKey(RegistryHive hive, RegistryView view)
        => RegistryKey.OpenBaseKey(hive, view);

    /// <summary>Returns true if the sub-key exists.</summary>
    public static bool KeyExists(RegistryHive hive, string subKey, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var baseKey = BaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads a value, or null when the key/value is missing or unreadable.</summary>
    public static object? GetValue(RegistryHive hive, string subKey, string? name, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var baseKey = BaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name);
        }
        catch
        {
            return null;
        }
    }

    public static string? GetString(RegistryHive hive, string subKey, string? name, RegistryView view = RegistryView.Registry64)
        => GetValue(hive, subKey, name, view)?.ToString();

    public static int? GetDword(RegistryHive hive, string subKey, string? name, RegistryView view = RegistryView.Registry64)
    {
        var value = GetValue(hive, subKey, name, view);
        return value is null ? null : Convert.ToInt32(value);
    }

    /// <summary>Creates <paramref name="subKey"/> if needed and writes a value. Returns success.</summary>
    public static bool SetValue(
        RegistryHive hive,
        string subKey,
        string? name,
        object value,
        RegistryValueKind kind,
        RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var baseKey = BaseKey(hive, view);
            using var key = baseKey.CreateSubKey(subKey, writable: true);
            if (key is null) return false;
            key.SetValue(name, value, kind);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetDword(RegistryHive hive, string subKey, string name, int value, RegistryView view = RegistryView.Registry64)
        => SetValue(hive, subKey, name, value, RegistryValueKind.DWord, view);

    public static bool SetString(RegistryHive hive, string subKey, string name, string value, RegistryValueKind kind = RegistryValueKind.String, RegistryView view = RegistryView.Registry64)
        => SetValue(hive, subKey, name, value, kind, view);

    /// <summary>Deletes a single value. Returns true if deleted or already absent.</summary>
    public static bool DeleteValue(RegistryHive hive, string subKey, string name, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var baseKey = BaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: true);
            if (key is null) return true;
            key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
