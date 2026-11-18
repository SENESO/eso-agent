using System.Globalization;
using EsoAgent.Core;
using Microsoft.Win32;

namespace EsoAgent.Windows;

/// <summary>
/// Windows IRegistryAccess over Microsoft.Win32. Paths look like
/// "HKLM\SOFTWARE\Foo" or "HKCU\Software\Bar"; the value name "" or
/// "(Default)" addresses the key's default value. Set captures the value
/// before and after so the caller (and the audit log) can see old → new.
/// </summary>
public sealed class WindowsRegistryAccess : IRegistryAccess
{
    public RegistryGetResponse Get(string path, string name)
    {
        var (baseKey, subPath) = OpenBase(path);
        using (baseKey)
        {
            using var key = subPath.Length == 0 ? null : baseKey.OpenSubKey(subPath, writable: false);
            // A path naming just a hive root reads that root key itself.
            var target = key ?? (subPath.Length == 0 ? baseKey : null);
            if (target == null)
                return new RegistryGetResponse { KeyExists = false, ValueExists = false };
            return ReadValue(target, NormalizeName(name));
        }
    }

    public (RegistryGetResponse Before, RegistryGetResponse After) Set(RegistrySetRequest request)
    {
        var before = Get(request.Path, request.Name);

        var (baseKey, subPath) = OpenBase(request.Path);
        using (baseKey)
        {
            using var key = subPath.Length == 0 ? baseKey : baseKey.CreateSubKey(subPath, writable: true);
            var (value, kind) = ConvertValue(request.Kind, request.Value);
            // Win32 semantics: SetValue("", ...) writes the default value.
            key.SetValue(NormalizeName(request.Name), value, kind);
        }

        var after = Get(request.Path, request.Name);
        return (before, after);
    }

    // ---------- helpers ----------

    private static string NormalizeName(string name)
        => string.IsNullOrEmpty(name) || name == "(Default)" ? "" : name;

    private static (RegistryKey BaseKey, string SubPath) OpenBase(string path)
    {
        var trimmed = path.Trim().Trim('\\');
        var sep = trimmed.IndexOf('\\');
        var root = (sep < 0 ? trimmed : trimmed[..sep]).ToUpperInvariant();
        var subPath = sep < 0 ? "" : trimmed[(sep + 1)..];

        var hive = root switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
            _ => throw new ArgumentException($"Unknown registry root '{root}'. Use HKLM, HKCU, HKCR, HKU or HKCC."),
        };
        return (RegistryKey.OpenBaseKey(hive, RegistryView.Default), subPath);
    }

    private static RegistryGetResponse ReadValue(RegistryKey key, string normalizedName)
    {
        var exists = key.GetValueNames().Any(n => string.Equals(n, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            return new RegistryGetResponse { KeyExists = true, ValueExists = false };

        var kind = key.GetValueKind(normalizedName);
        // DoNotExpandEnvironmentNames: ExpandString values render unexpanded, as stored.
        var raw = key.GetValue(normalizedName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var rendered = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => raw as string ?? "",
            RegistryValueKind.DWord => ((int)raw!).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => ((long)raw!).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.MultiString => string.Join("\n", (string[])raw!),
            RegistryValueKind.Binary => Convert.ToBase64String((byte[])raw!),
            RegistryValueKind.None => "",
            _ => raw?.ToString() ?? "",
        };
        return new RegistryGetResponse
        {
            KeyExists = true,
            ValueExists = true,
            Kind = kind.ToString(),
            Value = rendered,
        };
    }

    private static (object Value, RegistryValueKind Kind) ConvertValue(string kindName, string value)
    {
        switch (kindName.Trim().ToLowerInvariant())
        {
            case "string":
                return (value, RegistryValueKind.String);
            case "expandstring":
                return (value, RegistryValueKind.ExpandString);
            case "dword":
                // Accept signed or unsigned 32-bit input.
                if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return (i, RegistryValueKind.DWord);
                if (uint.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
                    return (unchecked((int)u), RegistryValueKind.DWord);
                throw new FormatException($"'{value}' is not a valid DWord.");
            case "qword":
                if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return (l, RegistryValueKind.QWord);
                if (ulong.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul))
                    return (unchecked((long)ul), RegistryValueKind.QWord);
                throw new FormatException($"'{value}' is not a valid QWord.");
            case "multistring":
                return (value.Split('\n'), RegistryValueKind.MultiString);
            case "binary":
                return (Convert.FromBase64String(value), RegistryValueKind.Binary);
            default:
                throw new ArgumentException($"Unknown registry value kind '{kindName}'. Use String, ExpandString, DWord, QWord, MultiString or Binary.");
        }
    }
}
