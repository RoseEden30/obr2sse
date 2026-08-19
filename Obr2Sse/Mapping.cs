using System.Text.Json;
using System.Text.Json.Serialization;

namespace Obr2Sse;

/// One weapon to convert: which Oblivion mesh goes into which Skyrim template.
public sealed class WeaponMapping
{
    /// Asset name in Oblivion, e.g. SM_Glass_Dagger.
    public required string Source { get; set; }

    /// Path of the vanilla mesh to use as a template, e.g. meshes\weapons\glass\glassdagger.nif.
    public required string Template { get; set; }

    /// Set when the pair was guessed rather than chosen, so it can be reviewed before running.
    public string? Note { get; set; }
}

public sealed class MappingFile
{
    public List<WeaponMapping> Weapons { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MappingFile Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MappingFile>(json, Options)
               ?? throw new InvalidDataException($"Empty mapping file: {path}");
    }

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}

/// Guesses which Oblivion weapon corresponds to which Skyrim one.
///
/// Both games name weapons by material and type, which is what makes this possible at all, but the
/// two catalogues only partly overlap: Oblivion has Akaviri and Amber, Skyrim has Nordic and Stalhrim.
/// Anything without a counterpart is left out rather than forced onto a wrong template.
public static class Matcher
{
    /// Materials that exist in both games, mapped from Oblivion's folder to Skyrim's.
    private static readonly Dictionary<string, string> Materials = new(StringComparer.OrdinalIgnoreCase)
    {
        ["daedric"] = "daedric",
        ["dwarven"] = "dwarven",
        ["ebony"] = "ebony",
        ["elven"] = "elven",
        ["glass"] = "glass",
        ["iron"] = "iron",
        ["orcish"] = "orcish",
        ["silver"] = "silver",
        ["steel"] = "steel",
    };

    /// Oblivion's weapon types against the words Skyrim uses in its file names, with the words that
    /// must not match. Skyrim's one handed sword is "sword", but so is the tail of "greatsword".
    private static readonly Dictionary<string, (string[] Wanted, string[] Rejected)> Types =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["LongSword"] = (new[] { "sword" }, new[] { "greatsword" }),
        ["Claymore"] = (new[] { "greatsword" }, Array.Empty<string>()),
        ["Dagger"] = (new[] { "dagger" }, Array.Empty<string>()),
        ["WarAxe"] = (new[] { "waraxe" }, Array.Empty<string>()),
        ["BattleAxe"] = (new[] { "battleaxe" }, Array.Empty<string>()),
        ["Mace"] = (new[] { "mace" }, Array.Empty<string>()),
        ["WarHammer"] = (new[] { "warhammer" }, Array.Empty<string>()),
    };

    public static MappingFile Propose(IEnumerable<string> obrMeshes, IEnumerable<string> skyrimMeshes)
    {
        var skyrim = skyrimMeshes.ToList();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new MappingFile();

        foreach (var packagePath in obrMeshes)
        {
            var name = Path.GetFileName(packagePath);

            // Scabbards are part of the weapon's own NIF in Skyrim, so they are not weapons here.
            if (name.EndsWith("_Scabbard", StringComparison.OrdinalIgnoreCase))
                continue;

            var material = MatchMaterial(packagePath);
            var type = MatchType(name);

            if (material is null || type is null)
                continue;

            // The name has to be material plus type and nothing else. Uniques like EbonyBlade or
            // RoseofSithis carry a material word but are their own weapon, not a variant of it.
            if (!name.Equals($"SM_{material}_{type}", StringComparison.OrdinalIgnoreCase))
                continue;

            var (wanted, rejected) = Types[type];

            var template = skyrim.FirstOrDefault(path =>
            {
                if (!path.Contains($"\\{material}\\", StringComparison.OrdinalIgnoreCase))
                    return false;

                var file = Path.GetFileNameWithoutExtension(path);

                if (rejected.Any(word => file.EndsWith(word, StringComparison.OrdinalIgnoreCase)))
                    return false;

                return wanted.Any(word => file.EndsWith(word, StringComparison.OrdinalIgnoreCase));
            });

            if (template is null)
                continue;

            // One template, one weapon: a second source would just overwrite the first.
            if (!taken.Add(template))
                continue;

            result.Weapons.Add(new WeaponMapping
            {
                Source = name,
                Template = template,
                Note = "guessed, check before running",
            });
        }

        return result;
    }

    /// Oblivion sorts most of its weapons into a folder per material, but not all of them: some sit
    /// outside that layout and carry the material in the asset name instead. Both are read here so
    /// the rest does not have to care which one an asset came from.
    private static string? MatchMaterial(string packagePath)
    {
        var name = Path.GetFileName(packagePath);

        foreach (var (obr, skyrim) in Materials)
        {
            if (packagePath.Contains($"/weapons/{obr}/", StringComparison.OrdinalIgnoreCase))
                return skyrim;

            if (name.StartsWith($"SM_{obr}_", StringComparison.OrdinalIgnoreCase))
                return skyrim;
        }

        return null;
    }

    private static string? MatchType(string name)
    {
        // Longest first: ShortSword would otherwise match Sword.
        foreach (var type in Types.Keys.OrderByDescending(t => t.Length))
        {
            if (name.EndsWith("_" + type, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(type, StringComparison.OrdinalIgnoreCase))
                return type;
        }

        return null;
    }
}
