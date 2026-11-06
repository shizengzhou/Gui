using System;
using System.IO;
using Microsoft.Win32;

namespace Gui.Term;

/// <summary>
/// Crash dump collection. A XAML callback that throws turns into an application fail-fast
/// (0xC000027B) with no managed stack, so the only way to see what happened is a full
/// process dump. Windows Error Reporting can write one for us, but it has to be told to:
/// this registers the app under the current user's LocalDumps key, the same way Windows
/// Terminal asks its users to enable Terminal.wprp style traces.
/// </summary>
internal static class CrashDump
{
    private const string LocalDumps =
        @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    /// <summary>Where the dumps land.</summary>
    public static string DumpFolder => Path.Combine(Path.GetTempPath(), "gui-dumps");

    /// <summary>
    /// Registers this executable for full crash dumps. Returns false when the registry
    /// cannot be written; the app keeps running either way.
    /// </summary>
    public static bool Enable()
    {
        try
        {
            string executable = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Gui";
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
                LocalDumps + "\\" + executable, writable: true);
            if (key is null) return false;

            Directory.CreateDirectory(DumpFolder);

            key.SetValue("DumpFolder", DumpFolder, RegistryValueKind.String);
            key.SetValue("DumpType", 2, RegistryValueKind.DWord);   // 2 = full dump
            key.SetValue("DumpCount", 10, RegistryValueKind.DWord); // keep the last ten
            return true;
        }
        catch (Exception error)
        {
            Diag.LogException("CrashDump.Enable", error);
            return false;
        }
    }
}
