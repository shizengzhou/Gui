using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Gui.Term;

/// <summary>A shell this machine offers, launched the way Windows Terminal launches it.</summary>
internal sealed record ShellProfile(string Name, string Command, string? WorkingDirectory);

/// <summary>
/// The two shells Windows Terminal adds beyond the Windows defaults, found the same way it
/// finds them: Git Bash from the Git for Windows install path (Windows Terminal picks it up
/// through the fragment Git's installer writes) and one entry per WSL distribution from
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss, skipping the utility distributions
/// Docker and Rancher create.
///
/// The command lines mirror Windows Terminal's: Git Bash runs its own "bash.exe -i -l" with
/// the profile directory, and a distribution runs wsl.exe by absolute path so a stray copy on
/// PATH cannot hijack it. Windows Terminal asks for "~" as the starting directory; a Linux
/// path cannot be a CreateProcess directory, so the distro's home comes from wsl's own --cd.
///
/// The command palette does not list these yet: discovery is in place, but the rows are held
/// back until the launch path has been exercised on a machine with both shells installed.
/// </summary>
internal static class ShellProfiles
{
    private const string GitKey = @"SOFTWARE\GitForWindows";
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    /// <summary>Discovered once per process; a new distribution is a restart away.</summary>
    private static readonly Lazy<IReadOnlyList<ShellProfile>> Cached = new(Discover);

    public static IReadOnlyList<ShellProfile> Known => Cached.Value;

    private static IReadOnlyList<ShellProfile> Discover()
    {
        var profiles = new List<ShellProfile>(3);
        try
        {
            if (GitBash() is { } git) profiles.Add(git);
            foreach (string distro in WslDistributions())
                profiles.Add(new ShellProfile(distro, WslCommand(distro), null));
        }
        catch (Exception error)
        {
            // Discovery is a convenience: a locked-down registry must not stop a window.
            Diag.Warn("shell-profiles", error.Message);
        }

        if (profiles.Count == 0) Diag.Debug("shell-profiles", "none");
        else Diag.Debug("shell-profiles", string.Join(", ", profiles.ConvertAll(p => p.Name)));
        return profiles;
    }

    private static ShellProfile? GitBash()
    {
        string? install = ReadString(Registry.LocalMachine, GitKey, "InstallPath")
            ?? ReadString(Registry.CurrentUser, GitKey, "InstallPath");
        if (string.IsNullOrEmpty(install))
        {
            install = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git");
        }

        string bash = Path.Combine(install, "bin", "bash.exe");
        if (!File.Exists(bash)) return null;

        // The command line Git's own Windows Terminal fragment uses.
        string command = "\"" + bash.Replace('\\', '/') + "\" -i -l";
        return new ShellProfile("Git Bash", command,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private static IEnumerable<string> WslDistributions()
    {
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(LxssKey);
        if (root is null) yield break;

        foreach (string guid in root.GetSubKeyNames())
        {
            // Distros are stored as {GUID} subkeys; anything else under Lxss is not one.
            if (guid.Length != 38 || guid[0] != '{' || guid[^1] != '}') continue;

            string? name;
            using (RegistryKey? distro = root.OpenSubKey(guid))
                name = distro?.GetValue("DistributionName") as string;

            if (string.IsNullOrEmpty(name) || IsUtilityDistribution(name)) continue;
            yield return name;
        }
    }

    /// <summary>Docker and Rancher keep their own distros here; they are not user-facing.</summary>
    internal static bool IsUtilityDistribution(string name) =>
        name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("rancher-desktop", StringComparison.OrdinalIgnoreCase);

    private static string WslCommand(string distro)
    {
        string command = Path.Combine(Environment.SystemDirectory, "wsl.exe") + " -d " + distro;
        // --cd arrived with Windows 10 2004; older builds fall back to the pane's directory.
        if (Environment.OSVersion.Version.Build >= 19041) command += " --cd ~";
        return command;
    }

    private static string? ReadString(RegistryKey hive, string path, string value)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(path);
            return key?.GetValue(value) as string;
        }
        catch
        {
            return null;
        }
    }
}
