namespace Obr2Sse;

/// Checks that the Oblivion Remastered paks can be opened and geometry actually read out of them.
///
/// Given an asset name it inspects that one instead of the default sample, and lists the textures
/// each section's material references. That is the only way to see why a converted weapon ended up
/// painted with a texture set you did not expect.
public static class ProbeObr
{
    public static int Run(string gamePath, string? mappingsPath = null, string? sample = null)
    {
        Console.WriteLine($"opening {gamePath}");

        using var obr = new ObrData(gamePath, mappingsPath);

        Console.WriteLine($"{obr.FileCount} files indexed");

        var weapons = obr.WeaponMeshes().ToList();
        Console.WriteLine($"{weapons.Count} weapon meshes");

        var target = sample is null
            ? weapons.FirstOrDefault(p => p.EndsWith("SM_Chillrend_Sword", StringComparison.OrdinalIgnoreCase))
            : weapons.FirstOrDefault(p => Path.GetFileName(p).Equals(sample, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            Console.WriteLine(sample is null ? "no sample mesh to load" : $"no mesh named {sample}");
            return sample is null ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine($"loading {target}");

        var mesh = obr.LoadStaticMesh(target);
        if (mesh is null)
        {
            Console.WriteLine("  returned null");
            return 1;
        }

        var primitives = ObrMesh.Load(mesh, Path.GetFileName(target));
        Console.WriteLine($"  {primitives.Count} primitives");
        Console.WriteLine();

        foreach (var prim in primitives)
        {
            var (min, max) = prim.Bounds();
            var size = max - min;

            Console.WriteLine($"  material {prim.MaterialIndex} ({prim.MaterialName})");
            Console.WriteLine($"    {prim.Positions.Length} verts, {prim.TriangleCount} tris, " +
                              $"{prim.Normals.Length} normals, {prim.TexCoords.Length} uvs");
            Console.WriteLine($"    size {size.X:F3} x {size.Y:F3} x {size.Z:F3}");
            Console.WriteLine($"    from {min.X:F3},{min.Y:F3},{min.Z:F3} to {max.X:F3},{max.Y:F3},{max.Z:F3}");

            // The converter paints one material's textures onto the weapon; printing them all shows
            // which section carries the set it should pick.
            foreach (var (name, texture) in obr.MaterialTextures(ObrMesh.Material(mesh, prim.MaterialIndex)))
                Console.WriteLine($"    {name,-22} {texture.Name}");
        }

        return 0;
    }
}
