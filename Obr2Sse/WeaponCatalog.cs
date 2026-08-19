namespace Obr2Sse;

/// The kind of weapon, which decides the animation, the skeleton it is injected into, and - for the
/// blades - whether it carries a sheath. Oblivion names its assets by material and type, so the type
/// is read straight off the asset name.
public enum WeaponType
{
    Sword,      // one handed: LongSword, ShortSword, Cutlass, unique blades
    Dagger,
    WarAxe,     // one handed axe, and the Shivering Isles cleaver
    Mace,       // one handed blunt, and the odd club
    Greatsword, // two handed: Claymore
    BattleAxe,  // two handed axe
    Warhammer,  // two handed blunt, and the farm poles
    Staff,      // magic staff, a weapon record with a staff enchantment
    Bow,        // skinned, out of scope
    Unknown,
}

/// Classifies an Oblivion weapon asset and resolves what it should be built against.
///
/// Standalone conversion injects every weapon into the steel skeleton of its type: present in every
/// install, and carrying the right shape roles (sheath, blood decals, node tree). The geometry is fit
/// onto the skeleton and its textures override the steel ones, so one rule covers every weapon.
public static class WeaponCatalog
{
    /// Type tokens as Oblivion spells them, longest first so ShortSword is tried before Sword and
    /// BattleAxe before Axe. Each maps to the Skyrim weapon class it stands in for.
    private static readonly (string Token, WeaponType Type)[] TypeTokens =
    {
        ("ShortSword", WeaponType.Sword),
        ("LongSword", WeaponType.Sword),
        ("GreatSword", WeaponType.Greatsword),
        ("BattleAxe", WeaponType.BattleAxe),
        ("WarHammer", WeaponType.Warhammer),
        ("WarAxe", WeaponType.WarAxe),
        ("Claymore", WeaponType.Greatsword),
        ("Cutlass", WeaponType.Sword),
        ("Cleaver", WeaponType.WarAxe),
        ("Dagger", WeaponType.Dagger),
        ("Sword", WeaponType.Sword),
        ("Blade", WeaponType.Sword),
        ("Mace", WeaponType.Mace),
        ("Club", WeaponType.Mace),
        ("Hoe", WeaponType.Warhammer),
        ("Rake", WeaponType.Warhammer),
        ("Staff", WeaponType.Staff),
        ("Bow", WeaponType.Bow),
    };

    /// A few uniques carry no type token in their name. Best-effort placement so they still convert.
    private static readonly Dictionary<string, WeaponType> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SM_WitSplinter"] = WeaponType.Mace,
        ["SM_forkofhorripilation"] = WeaponType.Dagger,
        ["SM_bow"] = WeaponType.Bow,
        ["SM_club"] = WeaponType.Mace,
        ["SM_SE11ScreamingBranch"] = WeaponType.Staff,
    };

    /// The steel skeleton each type is injected into. Verified present in a stock Skyrim SE install,
    /// and each carries the shape roles the converter needs: the two blades a Scb sheath.
    private static readonly Dictionary<WeaponType, string> Skeletons = new()
    {
        [WeaponType.Sword] = @"meshes\weapons\steel\steelsword.nif",
        [WeaponType.Dagger] = @"meshes\weapons\steel\steeldagger.nif",
        [WeaponType.WarAxe] = @"meshes\weapons\steel\steelwaraxe.nif",
        [WeaponType.Mace] = @"meshes\weapons\steel\steelmace.nif",
        [WeaponType.Greatsword] = @"meshes\weapons\steel\steelgreatsword.nif",
        [WeaponType.BattleAxe] = @"meshes\weapons\steel\steelbattleaxe.nif",
        [WeaponType.Warhammer] = @"meshes\weapons\steel\steelwarhammer.nif",
        // A staff is a rigid, non-skinned shape, so it injects exactly like a weapon.
        [WeaponType.Staff] = @"meshes\weapons\dragonpriest\dragonprieststaff3rd.nif",
    };

    /// The folder a type's output sits in, under meshes\obr2sse and textures\obr2sse. Laid out the way
    /// Bethesda organises its own tree: weapons and staves under weapons\.
    private static readonly Dictionary<WeaponType, string> Folders = new()
    {
        [WeaponType.Sword] = @"weapons\sword",
        [WeaponType.Dagger] = @"weapons\dagger",
        [WeaponType.WarAxe] = @"weapons\waraxe",
        [WeaponType.Mace] = @"weapons\mace",
        [WeaponType.Greatsword] = @"weapons\greatsword",
        [WeaponType.BattleAxe] = @"weapons\battleaxe",
        [WeaponType.Warhammer] = @"weapons\warhammer",
        [WeaponType.Staff] = @"weapons\staff",
        [WeaponType.Bow] = @"weapons\bow",
        [WeaponType.Unknown] = "misc",
    };

    /// Uniques with a base-game counterpart of a matching class, so the new record inherits its stats.
    /// Base game only, or the plugin would gain a Creation Club master. The class has to agree: Umbra
    /// and the Ebony Blade are one handed in Oblivion but greatswords in Skyrim, so they fall to the
    /// material tier instead. Substring match on the name.
    private static readonly (string Fragment, WeaponType Type, string EditorId)[] UniqueRecords =
    {
        ("MehrunesRazor", WeaponType.Dagger, "DA07MehrunesRazor"),
        ("Volendrung", WeaponType.Warhammer, "DA06Volendrung"),
        ("MolagBal", WeaponType.Mace, "DA10MaceofMolagBal"),
        ("Chillrend", WeaponType.Sword, "TG07Chillrend002"),
        // Staves that Skyrim carries as its own artifact, so they keep its stats and signature effect
        // rather than a generic base.
        ("Wabbajack", WeaponType.Staff, "DA15Wabbajack"),
        ("SkullOfCorruption", WeaponType.Staff, "DA16SkullofCorruption"),
        ("SanguineRose", WeaponType.Staff, "DA14SanguineRose"),
        // Not Akaviri: MQ203AkaviriKatana is Dragonbane, an enchanted named blade, not a generic
        // katana. Borrowing it would hand every Akaviri sword Dragonbane's enchantment and its "extra
        // damage to dragons" description. The Akaviri weapons stay steel-tier plain katanas instead.
    };

    /// The Skyrim material tier a weapon's stats are drawn from, so it fights like the vanilla weapon
    /// of its material. Shared materials map to themselves; Oblivion-only ones map to the nearest tier
    /// by lore and power (Amber and Golden Saint to glass, Madness to ebony, Grummite to iron). Scanned
    /// longest first; default is steel. Every tier is Skyrim.esm.
    private static readonly (string Keyword, string Material)[] MaterialTiers =
    {
        ("MehrunesDagon", "Daedric"),
        ("ClavicusUmbra", "Daedric"),
        ("Goldbrand", "Daedric"),
        ("Akatosh", "Daedric"),
        ("Daedric", "Daedric"),
        ("Umbra", "Daedric"),
        ("EbonyBlade", "Ebony"),
        ("DarkSeducer", "Ebony"),
        ("Madness", "Ebony"),
        ("Shadow", "Ebony"),
        ("Duskfang", "Ebony"),
        ("Dawnfang", "Ebony"),
        ("Ebony", "Ebony"),
        ("GoldenSaint", "Glass"),
        ("Amber", "Glass"),
        ("Glass", "Glass"),
        ("Elven", "Elven"),
        ("Dwarven", "Dwarven"),
        ("Orcish", "Orcish"),
        ("Silver", "Steel"),
        ("Akaviri", "Steel"),
        ("Steel", "Steel"),
        ("Grummite", "Iron"),
        ("Farm", "Iron"),
        ("Iron", "Iron"),
    };

    /// The vanilla editor-id fragment for a type, as Skyrim spells it in weapon records: DaedricMace,
    /// GlassBattleaxe, SteelWarAxe. Verified against the base game's weapon set.
    private static string TypeWord(WeaponType type) => type switch
    {
        WeaponType.Sword => "Sword",
        WeaponType.Dagger => "Dagger",
        WeaponType.WarAxe => "WarAxe",
        WeaponType.Mace => "Mace",
        WeaponType.Greatsword => "Greatsword",
        WeaponType.BattleAxe => "Battleaxe",
        WeaponType.Warhammer => "Warhammer",
        _ => "Sword",
    };

    private static string MaterialTier(string source)
    {
        foreach (var (keyword, material) in MaterialTiers)
        {
            if (source.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return material;
        }

        return "Steel";
    }

    public static string MeshRoot => @"meshes\obr2sse";
    public static string TextureRoot => @"textures\obr2sse";

    public static WeaponType Classify(string source)
    {
        if (ByName.TryGetValue(source, out var byName))
            return byName;

        foreach (var (token, type) in TypeTokens)
        {
            if (source.EndsWith(token, StringComparison.OrdinalIgnoreCase) ||
                source.Contains("_" + token, StringComparison.OrdinalIgnoreCase))
                return type;
        }

        return WeaponType.Unknown;
    }

    /// The steel skeleton to inject a source into, or null for a type with no skeleton (a bow, or an
    /// unclassifiable asset), which the caller reports and skips.
    public static string? Skeleton(WeaponType type) =>
        Skeletons.TryGetValue(type, out var path) ? path : null;

    public static string Folder(WeaponType type) => Folders[type];

    /// The base weapon record a source inherits its stats from: the bespoke record of a matching-class
    /// base-game unique when there is one, otherwise the vanilla weapon of the source's material tier
    /// and type - GlassSword, DaedricMace, IronDagger. Always a base-game editor id, so the stats are
    /// graded like vanilla and the plugin depends on nothing but the base game.
    public static string StatRecord(string source, WeaponType type)
    {
        foreach (var (fragment, recordType, editorId) in UniqueRecords)
        {
            if (recordType == type && source.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return editorId;
        }

        // A generic staff builds on a plain staff template and gets its effect from its enchantment;
        // the destruction one is a bare shell with no enchantment of its own to inherit.
        if (type == WeaponType.Staff)
            return "StaffTemplateDestruction";

        return MaterialTier(source) + TypeWord(type);
    }

    /// The standalone mesh path a source is written to, relative to the output root. World is the
    /// third person model the weapon record points at; First is its first person twin, found by the
    /// engine through the weapon's first-person STAT.
    public static string WorldMesh(string source, WeaponType type) =>
        Path.Combine(MeshRoot, Folder(type), source.ToLowerInvariant() + ".nif");

    public static string FirstPersonMesh(string source, WeaponType type) =>
        Path.Combine(MeshRoot, Folder(type), "1stperson" + source.ToLowerInvariant() + ".nif");

    /// The same two paths as a weapon record stores them: relative to Data\meshes, backslashes.
    public static string WorldModel(string source, WeaponType type) =>
        Path.Combine("obr2sse", Folder(type), source.ToLowerInvariant() + ".nif");

    public static string FirstPersonModel(string source, WeaponType type) =>
        Path.Combine("obr2sse", Folder(type), "1stperson" + source.ToLowerInvariant() + ".nif");

    /// Where a source's textures go: unique per weapon, since many share one steel skeleton and a
    /// folder keyed on the skeleton would collide.
    public static string TextureFolder(string source, WeaponType type) =>
        Path.Combine(TextureRoot, Folder(type), source.ToLowerInvariant());

    /// Named weapons whose in-game name is not "material type": artifacts and uniques by their UESP
    /// name, and the Oblivion-specific forms (the Akaviri katana, the farm tools). Matched on the
    /// exact asset name.
    private static readonly Dictionary<string, string> UniqueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SM_Goldbrand_LongSword"] = "Goldbrand",
        ["SM_ClavicusUmbra_Sword"] = "Umbra",
        ["SM_Chillrend_Sword"] = "Chillrend",
        ["SM_MehrunesRazor_Dagger"] = "Mehrunes' Razor",
        ["SM_MolagBal_Mace"] = "Mace of Molag Bal",
        ["SM_Volendrung_WarHammer"] = "Volendrung",
        ["SM_EbonyBlade_LongSword"] = "Ebony Blade",
        ["SM_Duskfang_Sword"] = "Dawnfang",
        ["SM_Shadow_BattleAxe"] = "Shadowrend",
        ["SM_Shadow_LongSword"] = "Shadowrend",
        ["SM_Blackwater_Blade"] = "Blackwater Blade",
        ["SM_Thornblade_Sword"] = "Thornblade",
        ["SM_Rugdumph_Sword"] = "Rugdumph's Sword",
        ["SM_Agarmirs_Sword"] = "Agarmir's Sword",
        ["SM_TrophySword_Claymore"] = "Sword of Jyggalag",
        ["SM_KnightOfOrder_LongSword"] = "Sword of Order",
        ["SM_Syls_WarHammer"] = "Syl's Warhammer",
        ["SM_WitSplinter"] = "Witsplinter",
        ["SM_Amelion_Sword"] = "Amelion Longsword",
        ["SM_Silverchorrol_Sword"] = "Chorrol Silver Sword",
        ["SM_Apostle_Dagger"] = "Apostle Dagger",
        ["SM_Ghostly_Dagger"] = "Ghostly Dagger",
        ["SM_Dragonsword_LongSword"] = "Dragon Sword",
        ["SM_Adventurers_Sword"] = "Adventurer's Sword",
        ["SM_NDWeapon_LongSword"] = "Sacred Longsword",
        ["SM_NDWeapon_Mace"] = "Sacred Mace",
        ["SM_Akaviri_LongSword"] = "Akaviri Katana",
        ["SM_Akaviri_Claymore"] = "Akaviri Dai-Katana",
        ["SM_AkaviriRuined_LongSword"] = "Ruined Akaviri Katana",
        ["SM_Farm_Hoe"] = "Hoe",
        ["SM_Farm_Rake"] = "Rake",
        ["SM_club"] = "Club",
        ["SM_bow"] = "Bow",
        ["SM_forkofhorripilation"] = "Fork of Horripilation",

        // Staves.
        ["SM_Wabbajack_Staff"] = "Wabbajack",
        ["SM_Sheogorath_Staff"] = "Staff of Sheogorath",
        ["SM_SkullOfCorruption_Staff"] = "Skull of Corruption",
        ["SM_SanguineRose_Staff"] = "Sanguine Rose",
        ["SM_HrormirsIce_Staff"] = "Hrormir's Icestaff",
        ["SM_KingOfWorms_Staff"] = "Staff of Worms",
        ["SM_Everscamp_Staff"] = "Everscamp Staff",
        ["SM_Indarys_Staff"] = "Indarys Staff",
        ["SM_GrummiteObelisk_Staff"] = "Grummite Obelisk Staff",
        ["SM_GrummiteObeliskPriest_Staff"] = "Grummite Priest Staff",
        ["SM_SE11ScreamingBranch"] = "Screaming Branch",
        ["SM_Staff01"] = "Staff of Firebolts",
        ["SM_Staff02"] = "Staff of Frostbite",
        ["SM_Staff03"] = "Staff of Lightning",
    };

    /// The display name of a material, so the generated name reads as Oblivion wrote it. Scanned in
    /// order, longest and most specific first so Golden Saint beats a bare gold and Mehrunes Dagon is
    /// not shortened. The default is the first name token, title-cased, for anything not listed.
    private static readonly (string Keyword, string Display)[] MaterialNames =
    {
        ("GoldenSaint", "Golden Saint"),
        ("DarkSeducer", "Dark Seducer"),
        ("MehrunesDagon", "Mehrunes Dagon"),
        ("Daedric", "Daedric"),
        ("Dwarven", "Dwarven"),
        ("Elven", "Elven"),
        ("Glass", "Glass"),
        ("Ebony", "Ebony"),
        ("Silver", "Silver"),
        ("Steel", "Steel"),
        ("Iron", "Iron"),
        ("Amber", "Amber"),
        ("Madness", "Madness"),
        ("Grummite", "Grummite"),
        ("Akatosh", "Akatosh"),
        ("Shadow", "Shadow"),
    };

    /// The Oblivion display word for a type token in the asset name, keeping the distinctions Skyrim
    /// folds away - a long sword and a short sword and a cutlass are all one class but three names.
    private static readonly (string Token, string Display)[] TypeNames =
    {
        ("ShortSword", "Shortsword"),
        ("LongSword", "Longsword"),
        ("BattleAxe", "Battle Axe"),
        ("WarHammer", "Warhammer"),
        ("WarAxe", "War Axe"),
        ("GreatSword", "Claymore"),
        ("Claymore", "Claymore"),
        ("Cutlass", "Cutlass"),
        ("Cleaver", "Cleaver"),
        ("Dagger", "Dagger"),
        ("Sword", "Sword"),
        ("Mace", "Mace"),
        ("Staff", "Staff"),
    };

    /// The weapon's in-game name. A named unique keeps its own name; anything else is "material type"
    /// in Oblivion's own words - Fine Steel Longsword, Glass Claymore, Golden Saint War Axe - so it
    /// reads as an Oblivion weapon rather than a raw asset id, and does not collide with the vanilla
    /// Skyrim name it is not.
    public static string DisplayName(string source)
    {
        if (UniqueNames.TryGetValue(source, out var unique))
            return unique;

        bool fine = source.Contains("fine", StringComparison.OrdinalIgnoreCase);

        string material = MaterialNames
            .FirstOrDefault(m => source.Contains(m.Keyword, StringComparison.OrdinalIgnoreCase)).Display
            ?? DefaultMaterial(source);

        string typeWord = TypeNames
            .FirstOrDefault(t => source.Contains(t.Token, StringComparison.OrdinalIgnoreCase)).Display ?? "";

        var name = (fine ? "Fine " : "") + material + (typeWord.Length > 0 ? " " + typeWord : "");
        return name.Trim();
    }

    /// Falls back to the first underscore-separated token of the asset name, title-cased, for a
    /// material the table does not name.
    private static string DefaultMaterial(string source)
    {
        var name = source.StartsWith("SM_", StringComparison.OrdinalIgnoreCase) ? source[3..] : source;
        var token = name.Split('_')[0];
        return token.Length == 0 ? name : char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
    }

    /// The enchantment a named artifact carries and its charge, when Skyrim has no record to inherit
    /// one from. Each is a real base-game object effect matching the weapon's lore (Goldbrand burns,
    /// Umbra steals souls) at artifact-tier charge. Artifacts with their own base record are not listed.
    public static (string EditorId, ushort Charge)? Enchantment(string source) => source switch
    {
        "SM_EbonyBlade_LongSword" => ("DA08EbonyBladeTraditionalEnchantment", (ushort)3000),
        "SM_ClavicusUmbra_Sword" => ("EnchWeaponSoulTrap06", 3000),
        "SM_Goldbrand_LongSword" => ("EnchWeaponFireDamage06", 3000),
        "SM_Duskfang_Sword" => ("EnchWeaponFireDamage06", 3000),

        // Generic staves get a staff enchantment matching their Oblivion effect. The named artifacts
        // (Wabbajack, Skull of Corruption, Sanguine Rose) keep their own through their base record and
        // are not listed here.
        "SM_HrormirsIce_Staff" => ("StaffEnchParalyze", 3000),
        "SM_KingOfWorms_Staff" => ("StaffEnchReanimateCorpse", 3000),
        "SM_Everscamp_Staff" => ("StaffEnchConjureFamiliar", 3000),
        "SM_Sheogorath_Staff" => ("DA15StaffEnchWabbajack", 3000),
        "SM_SE11ScreamingBranch" => ("StaffEnchFear", 3000),
        "SM_GrummiteObelisk_Staff" => ("StaffEnchFrostbite", 3000),
        "SM_GrummiteObeliskPriest_Staff" => ("StaffEnchFrostbite", 3000),
        "SM_Indarys_Staff" => ("StaffEnchFirebolt", 3000),
        "SM_Staff01" => ("StaffEnchFirebolt", 3000),
        "SM_Staff02" => ("StaffEnchFrostbite", 3000),
        "SM_Staff03" => ("StaffEnchLightningBolt", 3000),
        _ => null,
    };

    /// A short item-card description for a named weapon. Only weapons with established lore carry one;
    /// plain material weapons have none, as in vanilla. Matched on the exact asset name.
    public static string? Description(string source) => source switch
    {
        "SM_Goldbrand_LongSword" => "Boethiah's fabled katana, its blade wreathed in fire.",
        "SM_ClavicusUmbra_Sword" => "A cursed sword that hungers for the souls of those it slays.",
        "SM_EbonyBlade_LongSword" => "An ebony katana of Mephala that drinks the health of the living.",
        "SM_Duskfang_Sword" => "A blade that grows stronger the more blood it drinks.",
        "SM_MehrunesRazor_Dagger" => "The Dagger of the Final Wound, said to slay any foe in a single stroke.",
        "SM_Volendrung_WarHammer" => "The Hammer of Might, a Dwemer warhammer sacred to Malacath.",
        "SM_MolagBal_Mace" => "The Mace of Molag Bal, which drains the strength and magicka of the living.",
        "SM_Chillrend_Sword" => "A blade of enchanted glass, cold as glacial ice.",
        "SM_Shadow_BattleAxe" => "Shadowrend, a weapon wrought of living shadow.",
        "SM_Shadow_LongSword" => "Shadowrend, a weapon wrought of living shadow.",
        "SM_Akaviri_LongSword" => "A curved sword of Akaviri steel, favored by the Blades.",
        "SM_Akaviri_Claymore" => "A great two-handed blade of Akaviri design.",
        "SM_AkaviriRuined_LongSword" => "An ancient Akaviri katana, worn by the centuries.",

        "SM_Wabbajack_Staff" => "Sheogorath's staff of madness, its magic never twice the same.",
        "SM_Sheogorath_Staff" => "The staff of the Mad God, wild and unpredictable.",
        "SM_SkullOfCorruption_Staff" => "A staff that conjures a twisted mirror of its victim.",
        "SM_SanguineRose_Staff" => "Sanguine's staff, which calls a Dremora to fight at your side.",
        "SM_HrormirsIce_Staff" => "An ancient staff of ice that holds its foes fast.",
        "SM_KingOfWorms_Staff" => "The staff of Mannimarco, steeped in necromancy.",
        _ => null,
    };

    /// Sources left out of the standalone sweep. Adventurer's Sword is a near-identical duplicate of
    /// Duskfang, one model under two names. Duskfang (Dawnfang) and the plain steel battleaxe and
    /// waraxe decode from OBR with torn geometry - real cracks in the blade - so they are dropped;
    /// their Fine variants stay, and the replacer uses those Fine meshes for the vanilla axes.
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "SM_Adventurers_Sword",
        "SM_Duskfang_Sword",
        "SM_Steel_BattleAxe",
        "SM_Steel_WarAxe",
    };

    /// Staves whose OBR mesh is already authored head-up, so they must not get the end-for-end turn the
    /// other staves need. Without this they come out upside down while the rest are correct.
    private static readonly HashSet<string> StaffKeepOrientation = new(StringComparer.OrdinalIgnoreCase)
    {
        "SM_Wabbajack_Staff",
        "SM_SE11ScreamingBranch",
    };

    /// Whether a staff source needs turning end for end (true for most, false for the few authored the
    /// right way up already).
    public static bool StaffNeedsFlip(string source) => !StaffKeepOrientation.Contains(source);

    /// One catalog entry: a source, the type it was read as, the skeleton it builds against, and the
    /// base-game record it inherits its stats from.
    public sealed record Entry(string Source, WeaponType Type, string Skeleton, string Record);

    /// Builds the full standalone catalog from every Oblivion weapon asset. Scabbards are dropped
    /// (they ride with their weapon), bows and unclassifiable assets are returned separately so the
    /// caller can report them rather than silently skip.
    public static (List<Entry> Entries, List<string> Skipped) Build(IEnumerable<string> obrWeaponAssets)
    {
        var entries = new List<Entry>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in obrWeaponAssets)
        {
            var source = Path.GetFileName(path);

            if (source.EndsWith("_Scabbard", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Dropped.Contains(source))
                continue;

            if (!seen.Add(source))
                continue;

            var type = Classify(source);
            var skeleton = Skeleton(type);

            if (skeleton is null)
            {
                skipped.Add($"{source} ({(type == WeaponType.Bow ? "bow, skinned - out of scope" : "unclassified")})");
                continue;
            }

            entries.Add(new Entry(source, type, skeleton, StatRecord(source, type)));
        }

        return (entries.OrderBy(e => e.Source, StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }
}
