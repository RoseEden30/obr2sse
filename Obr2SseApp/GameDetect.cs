using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Obr2SseApp;

/// Locates the two game installs from the registry and the Steam libraries. Each path is validated
/// before it is returned, so a bad guess comes back as null rather than a wrong folder.
public static class GameDetect
{
    /// A Skyrim install is valid if it has the Data folder the reader pulls meshes from.
    public static bool IsSkyrim(string path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "Data"));

    /// An Oblivion Remastered install is valid if it has the paks the reader mounts.
    public static bool IsOblivion(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Directory.Exists(Path.Combine(path, "OblivionRemastered", "Content", "Paks"));

    public static string? FindSkyrim()
    {
        // The installer records its own path; trust it when it still points at a real install.
        using (var key = Registry.LocalMachine.OpenSubKey(
                   @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition"))
        {
            if (key?.GetValue("Installed Path") is string registered && IsSkyrim(registered))
                return registered;
        }

        foreach (var common in SteamCommonFolders())
        {
            var path = Path.Combine(common, "Skyrim Special Edition");
            if (IsSkyrim(path))
                return path;
        }

        return null;
    }

    public static string? FindOblivion()
    {
        foreach (var common in SteamCommonFolders())
        {
            if (!Directory.Exists(common))
                continue;

            foreach (var dir in Directory.EnumerateDirectories(common, "*Oblivion Remastered*"))
            {
                if (IsOblivion(dir))
                    return dir;
            }
        }

        return null;
    }

    /// Every steamapps\common folder across all Steam libraries.
    private static IEnumerable<string> SteamCommonFolders()
    {
        var steam = SteamPath();
        if (steam is null)
            yield break;

        yield return Path.Combine(steam, "steamapps", "common");

        // Extra libraries on other drives are listed in libraryfolders.vdf as "path" entries.
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            yield break;

        foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
        {
            var library = match.Groups[1].Value.Replace(@"\\", @"\");
            yield return Path.Combine(library, "steamapps", "common");
        }
    }

    private static string? SteamPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
            return path.Replace('/', '\\');

        using var key64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        if (key64?.GetValue("InstallPath") is string install && Directory.Exists(install))
            return install;

        return null;
    }
}
