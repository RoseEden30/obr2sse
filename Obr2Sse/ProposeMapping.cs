namespace Obr2Sse;

/// Writes a starting mapping file by pairing the two games' weapon catalogues.
public static class ProposeMapping
{
    public static int Run(string skyrimPath, string oblivionPath, string mappingsPath, string outPath)
    {
        using var obr = new ObrData(oblivionPath, mappingsPath);
        using var skyrim = new SkyrimData(skyrimPath);

        var obrWeapons = obr.WeaponMeshes().ToList();
        var skyrimWeapons = skyrim.FilesUnder(Path.Combine("meshes", "weapons"), ".nif")
            .Where(p => !Path.GetFileName(p).StartsWith("1stperson", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"{obrWeapons.Count} Oblivion weapon meshes");
        Console.WriteLine($"{skyrimWeapons.Count} Skyrim weapon meshes");

        var mapping = Matcher.Propose(obrWeapons, skyrimWeapons);
        mapping.Save(outPath);

        Console.WriteLine();
        Console.WriteLine($"{mapping.Weapons.Count} pairs proposed");
        Console.WriteLine();

        foreach (var weapon in mapping.Weapons.Take(20))
            Console.WriteLine($"  {weapon.Source,-32} -> {weapon.Template}");

        if (mapping.Weapons.Count > 20)
            Console.WriteLine($"  ... and {mapping.Weapons.Count - 20} more");

        Console.WriteLine();
        Console.WriteLine($"written to {Path.GetFullPath(outPath)}");
        Console.WriteLine("review it before converting: every pair is a guess");

        return 0;
    }
}
