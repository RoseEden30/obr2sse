using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;

namespace Obr2Sse;

/// Reads assets straight out of an Oblivion Remastered install.
///
/// The paks are unencrypted but Oodle compressed, and use unversioned properties: nothing
/// deserializes without a mappings file.
public sealed class ObrData : IDisposable
{
    private readonly DefaultFileProvider _provider;

    public int FileCount => _provider.Files.Count;

    public ObrData(string gamePath, string? mappingsPath = null)
    {
        var paks = Path.Combine(gamePath, "OblivionRemastered", "Content", "Paks");
        if (!Directory.Exists(paks))
            throw new DirectoryNotFoundException($"No Paks folder under {gamePath}");

        _provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly,
                                            new VersionContainer(EGame.GAME_UE5_3));

        // Weapons are Nanite meshes: their real geometry lives in streamable pages, and only the
        // coarse (and torn) fallback mesh is exposed unless this is set before mounting.
        _provider.ReadNaniteData = true;

        _provider.Initialize();

        if (mappingsPath is not null && File.Exists(mappingsPath))
            _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath);

        _provider.Mount();
    }

    /// Package paths of every static mesh whose path contains the filter, without extension and
    /// deduplicated: the index lists .uasset and .ubulk separately for the same asset.
    ///
    /// SM_ is the only prefix that matters: everything the converter reads is a static mesh.
    public IEnumerable<string> Meshes(string? filter = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _provider.Files.Keys)
        {
            if (!Path.GetFileName(path).StartsWith("SM_", StringComparison.Ordinal))
                continue;

            if (filter is not null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            int dot = path.LastIndexOf('.');
            if (dot < 0)
                continue;

            var withoutExtension = path[..dot];
            if (seen.Add(withoutExtension))
                yield return withoutExtension;
        }
    }

    /// Everything the converter can source from: weapons and their scabbards, all of which Oblivion
    /// keeps in the one equipment tree.
    public IEnumerable<string> WeaponMeshes() => Meshes("/Equipment/weapons/");

    /// Magic staves, in their own equipment folder.
    public IEnumerable<string> StaffMeshes() => Meshes("/Equipment/staffs/");

    /// Every asset the standalone sweep covers: weapons and staves.
    public IEnumerable<string> StandaloneAssets() =>
        WeaponMeshes().Concat(StaffMeshes());

    public UStaticMesh? LoadStaticMesh(string packagePath)
    {
        // The name after the last slash is taken as the export name, but a few assets are indexed
        // under a filename whose casing does not match the export inside - SM_Dwarven_Shortsword on
        // disk against SM_Dwarven_ShortSword in the package. When the exact name misses, the package
        // is loaded whole and its first static mesh taken, which a weapon asset always holds one of.
        try
        {
            return _provider.LoadPackageObject<UStaticMesh>(packagePath);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (Exception)
        {
            try
            {
                return _provider.LoadPackage(packagePath).GetExports().OfType<UStaticMesh>().FirstOrDefault();
            }
            catch
            {
                // A duplicate under a differently-cased path, a stray entry: absent rather than fatal,
                // so one bad asset does not sink the batch.
                return null;
            }
        }
    }

    /// Textures a material actually references, keyed by parameter name. Going through the material
    /// matters: a weapon folder holds the textures of every weapon of that material, so picking by
    /// folder gives you whichever one happened to sort first.
    public Dictionary<string, UTexture2D> MaterialTextures(UMaterialInterface? material)
    {
        var result = new Dictionary<string, UTexture2D>(StringComparer.OrdinalIgnoreCase);
        if (material is null)
            return result;

        var parameters = new CMaterialParams2();
        material.GetParams(parameters, EMaterialDepth.AllLayersNoRef);

        foreach (var (name, texture) in parameters.Textures)
        {
            if (texture is UTexture2D texture2D)
                result[name] = texture2D;
        }

        return result;
    }

    public void Dispose() => _provider.Dispose();
}
