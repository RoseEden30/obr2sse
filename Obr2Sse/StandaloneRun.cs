namespace Obr2Sse;

/// Runs the standalone sweep: every Oblivion weapon into its own mesh, then one ESP that adds them
/// all as new craftable records. Shared by the command line and the GUI so both convert the same way.
public static class StandaloneRun
{
    public sealed record Result(
        int Converted,
        int Total,
        IReadOnlyList<string> Failures,
        IReadOnlyList<string> Unclassified,
        string? EspError);

    /// Converts every weapon and builds the ESP. onWeapon reports progress as each one lands. A
    /// cancellation is honoured between weapons: it stops before the next one and throws, so the
    /// caller can clean up rather than leave a half-built ESP.
    public static Result Execute(ObrData obr, Pipeline pipeline, string outputDir,
                                 Action<int, int, string>? onWeapon = null,
                                 CancellationToken cancel = default,
                                 string? skyrimDataFolder = null)
    {
        var (entries, unclassified) = WeaponCatalog.Build(obr.StandaloneAssets());

        var produced = new List<WeaponCatalog.Entry>();
        var failures = new List<string>();

        for (int i = 0; i < entries.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();

            var entry = entries[i];

            string? error;
            try
            {
                error = pipeline.ConvertStandalone(entry.Source, entry.Type, entry.Skeleton);
            }
            catch (Exception e)
            {
                // One bad asset must never sink the batch.
                error = e.Message;
            }

            if (error is null)
            {
                produced.Add(entry);
                onWeapon?.Invoke(i + 1, entries.Count, entry.Source);
            }
            else
            {
                failures.Add($"{entry.Source}: {error}");
            }
        }

        // The ESP only references what actually converted, so a failed weapon never leaves a record
        // pointing at a mesh that was not written.
        string? espError = null;
        if (produced.Count > 0)
        {
            try
            {
                EspBuilder.BuildStandalone(produced, Path.Combine(outputDir, "OBR2SSE - Weapons.esp"), skyrimDataFolder);
            }
            catch (Exception e)
            {
                espError = e.Message;
            }
        }

        return new Result(produced.Count, entries.Count, failures, unclassified, espError);
    }
}
