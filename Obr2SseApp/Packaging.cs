using System.IO.Compression;

namespace Obr2SseApp;

/// How the converted mod is delivered.
public enum OutputFormat
{
    /// A single .zip a mod manager installs directly.
    Zip,

    /// The meshes, textures and plugin written straight into a folder.
    Loose,
}

public static class Packaging
{
    /// Zips a converted mod folder so the archive root holds meshes\, textures\ and the plugin - the
    /// layout MO2 and Vortex install directly.
    public static void ZipMod(string modFolder, string zipPath)
    {
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(modFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }
}
