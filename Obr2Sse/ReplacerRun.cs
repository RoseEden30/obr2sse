namespace Obr2Sse;

/// Runs the replacer sweep: each mapped Oblivion weapon overwrites the vanilla mesh named in the
/// mapping, so every instance of that weapon takes on the imported look. No plugin is produced - the
/// meshes stand in for the vanilla files at their own paths. Shared by the command line and the GUI.
public static class ReplacerRun
{
    public sealed record Result(int Converted, int Total, IReadOnlyList<string> Failures);

    public static Result Execute(Pipeline pipeline, MappingFile mapping,
                                 Action<int, int, string>? onWeapon = null,
                                 CancellationToken cancel = default)
    {
        var weapons = mapping.Weapons;
        var failures = new List<string>();
        int converted = 0;

        for (int i = 0; i < weapons.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();

            var weapon = weapons[i];

            string? error;
            try
            {
                error = pipeline.Convert(weapon.Source, weapon.Template);
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (error is null)
            {
                converted++;
                onWeapon?.Invoke(i + 1, weapons.Count, weapon.Source);
            }
            else
            {
                failures.Add($"{weapon.Source}: {error}");
            }
        }

        return new Result(converted, weapons.Count, failures);
    }
}
