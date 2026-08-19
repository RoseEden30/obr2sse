using System.Reflection;

namespace Obr2SseApp;

/// Data files shipped inside the app so the user never has to find or supply them: the Oblivion type
/// mappings the reader needs, and the curated replacer mapping. Each is extracted to a temp file once,
/// since the code that uses them takes a path.
///
/// The .usmap holds only Unreal's reflection metadata - class, struct and property names - and no
/// game assets, so shipping it carries nothing proprietary of Oblivion's art or data.
public static class Mappings
{
    /// The Oblivion type mappings (.usmap), used to read every asset.
    public static string Path() => Extract(".usmap", "obr2sse.usmap");

    /// The curated Oblivion-to-Skyrim replacer mapping, used only in replacer mode.
    public static string ReplacerJsonPath() => Extract("weapons.json", "obr2sse_weapons.json");

    private static string Extract(string resourceSuffix, string fileName)
    {
        var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);

        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var file = File.Create(target);
        stream.CopyTo(file);

        return target;
    }
}
