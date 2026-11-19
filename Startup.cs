using System;
using Microsoft.Win32;

namespace Clipwise;

/// <summary>HKCU Run entry so Clipwise can start with Windows (no elevation needed).</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clipwise";

    /// <summary>The command currently registered to run at logon, if any.</summary>
    public static string? Command
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) as string;
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(Command);

    /// <summary>Add or remove the Run entry. Returns false when the registry
    /// refused the write, so callers can revert the menu state.</summary>
    public static bool Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return false;

            if (on)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
