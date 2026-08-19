using Obr2Sse;

namespace Obr2SseApp;

public enum ConversionMode
{
    /// Every weapon becomes a new craftable item under its own path, plus an ESP. Nothing vanilla is
    /// touched.
    Standalone,

    /// Each mapped weapon overwrites the vanilla mesh it stands in for. No plugin.
    Replacer,
}

/// The conversion the Convert button runs, with no UI attached so it can be driven and tested on its
/// own. Sweeps the Oblivion weapons into a Skyrim mod, then either leaves it as loose files or packs
/// it into a mod-manager archive.
///
/// Cancellation and failure leave nothing behind: a zip is built in a scratch folder that is deleted,
/// and any leftover scratch is swept when the app closes.
public static class Conversion
{
    public sealed record Result(bool Ok, string Message, string Reveal);

    /// The archive is named after the mode that built it, so a Standalone and a Replacer build sitting
    /// in the same folder never overwrite each other.
    public static string ArchiveName(ConversionMode mode) => mode == ConversionMode.Standalone
        ? "OBR2SSE - Weapons Standalone"
        : "OBR2SSE - Weapons Replacer";

    public static Result Run(string obrPath, string skyrimPath, string output,
                             ConversionMode mode, OutputFormat format, ObrMesh.MeshQuality quality,
                             Action<int, int, string>? progress = null,
                             CancellationToken cancel = default)
    {
        ObrMesh.Quality = quality;
        bool zip = format == OutputFormat.Zip;

        string workDir = zip
            ? System.IO.Path.Combine(Path.GetTempPath(), "obr2sse_build_" + Guid.NewGuid().ToString("N"))
            : output;
        Directory.CreateDirectory(workDir);

        try
        {
            int converted;
            string? espError = null;

            using (var obr = new ObrData(obrPath, Mappings.Path()))
            using (var skyrim = new SkyrimData(skyrimPath))
            {
                var pipeline = new Pipeline(obr, skyrim, workDir);

                if (mode == ConversionMode.Replacer)
                {
                    var mapping = MappingFile.Load(Mappings.ReplacerJsonPath());
                    converted = ReplacerRun.Execute(pipeline, mapping, progress, cancel).Converted;
                }
                else
                {
                    var result = StandaloneRun.Execute(obr, pipeline, workDir, progress, cancel, skyrim.DataFolder);
                    converted = result.Converted;
                    espError = result.EspError;
                }
            }

            string reveal;
            if (zip)
            {
                progress?.Invoke(-1, 0, "Packing archive…");
                Directory.CreateDirectory(output);
                reveal = System.IO.Path.Combine(output, ArchiveName(mode) + ".zip");
                Packaging.ZipMod(workDir, reveal);
                Delete(workDir);
            }
            else
            {
                reveal = mode == ConversionMode.Standalone
                    ? System.IO.Path.Combine(output, "OBR2SSE - Weapons.esp")
                    : output;
            }

            string message = espError is null
                ? $"Done - {converted} weapons."
                : $"Meshes done ({converted}), but the plugin failed: {espError}";

            return new Result(espError is null, message, reveal);
        }
        catch
        {
            // Cancelled or failed: tear down whatever was written so nothing partial is left.
            if (zip)
                Delete(workDir);
            else if (mode == ConversionMode.Standalone)
                CleanLoose(output);

            throw;
        }
    }

    /// Removes any leftover scratch build folders. Called when the app closes, so a cancel that could
    /// not delete its folder on the spot (a file still briefly held) is swept up anyway.
    public static void CleanTemp()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "obr2sse_build_*"))
                Delete(dir);
        }
        catch
        {
            // Nothing to do if temp cannot be enumerated.
        }
    }

    private static void CleanLoose(string output)
    {
        Delete(System.IO.Path.Combine(output, "meshes", "obr2sse"));
        Delete(System.IO.Path.Combine(output, "textures", "obr2sse"));

        var esp = System.IO.Path.Combine(output, "OBR2SSE - Weapons.esp");
        if (File.Exists(esp))
            File.Delete(esp);
    }

    private static void Delete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best effort: a scratch folder we could not remove is not worth failing over.
        }
    }
}
