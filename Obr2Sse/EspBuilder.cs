using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;

namespace Obr2Sse;

/// Generates a standalone ESP that adds the imported weapons as new records, so the mod stands on its
/// own: it never overwrites a vanilla mesh, and every OBR weapon becomes a new craftable item that
/// points at its own converted NIF under meshes\obr2sse. No Bethesda data is shipped - the base
/// records are read from the user's own install at build time and copied into the new plugin.
public static class EspBuilder
{
    /// The plugins a standalone mod may depend on: the base game and its official DLC, which every
    /// Skyrim SE install has. A base record duplicated from any of these adds no master beyond them;
    /// duplicating a Creation Club record would drag its plugin in as a master, which a standalone
    /// mod must never do. Records outside this set are refused and fall back to the steel skeleton.
    private static readonly HashSet<string> BaseGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    private static bool IsBaseGame(FormKey key) =>
        BaseGame.Contains(key.ModKey.ToString());

    private static bool IsBaseGame(IWeaponGetter record) => IsBaseGame(record.FormKey);

    // Skyrim's crafting benches, by form id: the forge creates a weapon, the grindstone tempers it.
    private const uint CraftingSmithingForge = 0x00088105;
    private const uint SharpeningWheel = 0x00088108;

    private static void AddItem(ConstructibleObject cobj, IItemGetter item, int count)
    {
        var entry = new ContainerEntry { Item = new ContainerItem { Count = count } };
        entry.Item.Item.SetTo(item.FormKey);
        cobj.Items!.Add(entry);
    }

    /// Builds the standalone plugin for a set of converted weapons. Each entry is a new weapon record
    /// duplicated from a base (a matching unique, else the vanilla weapon of its material and type), so
    /// it keeps that weapon's stats, keywords, enchantment and recipes; only the models, name and editor
    /// id change. A given data folder pins the read to the Skyrim the user pointed the converter at.
    public static int BuildStandalone(IReadOnlyList<WeaponCatalog.Entry> entries, string outPath,
                                      string? skyrimDataFolder = null)
    {
        using var env = skyrimDataFolder is null
            ? GameEnvironment.Typical.Skyrim(SkyrimRelease.SkyrimSE)
            : GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE)
                .WithTargetDataFolder(skyrimDataFolder)
                .Build();

        static string Norm(string p)
        {
            p = p.Replace('/', '\\').ToLowerInvariant();
            const string meshes = "meshes\\";
            return p.StartsWith(meshes) ? p[meshes.Length..] : p;
        }

        // Every weapon record keyed by its mesh and by its editor id, so a skeleton path or a named
        // unique both resolve to a base record.
        var byModel = new Dictionary<string, IWeaponGetter>();
        var byEditorId = new Dictionary<string, IWeaponGetter>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in env.LoadOrder.PriorityOrder.Weapon().WinningOverrides())
        {
            if (w.Model?.File.GivenPath is { } model)
                byModel[Norm(model)] = w;
            if (w.EditorID is { } id)
                byEditorId[id] = w;
        }

        var recipes = env.LoadOrder.PriorityOrder.ConstructibleObject().WinningOverrides().ToList();

        // Object effects (enchantments) keyed by editor id, so a named artifact can be given its
        // signature effect by name.
        var enchByEditorId = new Dictionary<string, IObjectEffectGetter>(StringComparer.OrdinalIgnoreCase);
        foreach (var oe in env.LoadOrder.PriorityOrder.ObjectEffect().WinningOverrides())
            if (oe.EditorID is { } id)
                enchByEditorId[id] = oe;

        // Staves have no smithable material, so they craft at the Staff Enchanter from a heart stone.
        // If the install lacks these records they just get no recipe, never a broken one.
        env.LinkCache.TryResolve<IKeywordGetter>("DLC2StaffEnchanter", out var staffBench);
        env.LinkCache.TryResolve<IItemGetter>("DLC2HeartStone", out var heartStone);
        env.LinkCache.TryResolve<IItemGetter>("SoulGemGrandFilled", out var grandSoul);

        var mod = new SkyrimMod(ModKey.FromNameAndExtension("OBR2SSE - Weapons.esp"), SkyrimRelease.SkyrimSE);

        int made = 0;
        var missing = new List<string>();
        var fellBack = new List<string>();

        foreach (var entry in entries)
        {
            // Stats come from the material-and-type vanilla record the catalog resolved (GlassSword,
            // DaedricMace), which grades the weapon like its material rather than the steel skeleton.
            // It must be base game: a Creation Club record would make the standalone plugin depend on
            // that CC content. If the intended record is somehow absent, fall back to the skeleton's
            // own steel record and say so, rather than pick something wrong or drop the weapon.
            IWeaponGetter? template = null;
            if (entry.Record is { } record &&
                byEditorId.TryGetValue(record, out var named) && IsBaseGame(named))
                template = named;

            if (template is null)
            {
                template = byModel.GetValueOrDefault(Norm(entry.Skeleton));
                if (template is not null && entry.Record is not null)
                    fellBack.Add($"{entry.Source}: {entry.Record} not found, used {template.EditorID}");
            }

            if (template is null || !IsBaseGame(template))
            {
                missing.Add($"{entry.Source} (no base-game record for {entry.Record ?? entry.Skeleton})");
                continue;
            }

            var weap = mod.Weapons.DuplicateInAsNewRecord(template);
            weap.EditorID = "OBR_" + entry.Source;
            weap.Name = WeaponCatalog.DisplayName(entry.Source);
            weap.Model!.File.GivenPath = WeaponCatalog.WorldModel(entry.Source, entry.Type);

            // A named artifact carries a short lore line on its item card; the plain weapons do not,
            // as in vanilla. The base record's own description (Dragonbane's, say) is always cleared
            // first, so nothing inherits a description that belongs to a different weapon.
            weap.Description = WeaponCatalog.Description(entry.Source);

            // The first person view is a separate STAT the weapon links to; without its own the weapon
            // would show the vanilla mesh in first person. Duplicate the base one and repoint it.
            if (template.FirstPersonModel.TryResolve<IStaticGetter>(env.LinkCache) is { } baseStat)
            {
                var stat = mod.Statics.DuplicateInAsNewRecord(baseStat);
                stat.EditorID = "OBR_1st" + entry.Source;
                if (stat.Model is not null)
                    // A staff has no separate first-person mesh converted, so it reuses its world one;
                    // held in hand it looks the same either way.
                    stat.Model.File.GivenPath = entry.Type == WeaponType.Staff
                        ? WeaponCatalog.WorldModel(entry.Source, entry.Type)
                        : WeaponCatalog.FirstPersonModel(entry.Source, entry.Type);
                weap.FirstPersonModel.SetTo(stat);
            }

            // A named artifact with no base-game record of its own is given its signature enchantment
            // here - a real object effect, so the plugin still leans on nothing but the base game. The
            // charge is the artifact tier the vanilla Daedric weapons use. Once enchanted it is no
            // longer re-enchantable at an altar, exactly as a vanilla artifact.
            bool enchanted = false;
            if (WeaponCatalog.Enchantment(entry.Source) is { } ench &&
                enchByEditorId.TryGetValue(ench.EditorId, out var effect) &&
                BaseGame.Contains(effect.FormKey.ModKey.ToString()))
            {
                weap.ObjectEffect.SetTo(effect);
                weap.EnchantmentAmount = ench.Charge;
                enchanted = true;
            }

            // Melee weapons forge and temper at their material tier, borrowing the vanilla recipes of the
            // matching material weapon (a glass sword at Glass Smithing, Goldbrand as a daedric blade).
            // This is what gives the named artifacts a forge recipe, since their own record has none.
            int copied = 0;
            if (entry.Type == WeaponType.Staff)
            {
                if (staffBench is not null && heartStone is not null)
                {
                    var cobj = mod.ConstructibleObjects.AddNew($"OBR_{entry.Source}_Recipe");
                    cobj.CreatedObject.SetTo(weap);
                    cobj.CreatedObjectCount = 1;
                    cobj.WorkbenchKeyword.SetTo(staffBench);
                    cobj.Items = new();
                    AddItem(cobj, heartStone, 1);
                    if (grandSoul is not null)
                        AddItem(cobj, grandSoul, 1);
                    copied = 1;
                }
            }
            else if (byEditorId.TryGetValue(WeaponCatalog.MaterialRecord(entry.Source, entry.Type), out var donor)
                     && IsBaseGame(donor))
            {
                foreach (var recipe in recipes.Where(c => c.CreatedObject.FormKey == donor.FormKey))
                {
                    var cobj = mod.ConstructibleObjects.DuplicateInAsNewRecord(recipe);
                    cobj.EditorID = $"OBR_{entry.Source}_Recipe{(copied++ == 0 ? "" : copied.ToString())}";
                    cobj.CreatedObject.SetTo(weap);
                }
            }

            made++;
        }

        // ESL-flag the plugin so it takes a light slot rather than a full one. Its few hundred new
        // records sit well under the format's ceiling; the guard only skips the flag in the unlikely
        // case the ids ever grow past what a light plugin can hold, rather than writing a broken one.
        bool esl = mod.CanBeSmallMaster;
        if (esl)
            mod.IsSmallMaster = true;

        mod.WriteToBinary(outPath, new BinaryWriteParameters
        {
            MastersListOrdering = new MastersListOrderingByLoadOrder(env.LoadOrder),
        });

        Console.WriteLine($"wrote {made} standalone weapons to {outPath}{(esl ? " (ESL-flagged)" : " (too many records for ESL)")}");
        if (fellBack.Count > 0)
        {
            Console.WriteLine($"{fellBack.Count} fell back to the steel record:");
            foreach (var f in fellBack)
                Console.WriteLine($"  {f}");
        }
        if (missing.Count > 0)
        {
            Console.WriteLine($"{missing.Count} had no base record:");
            foreach (var m in missing)
                Console.WriteLine($"  {m}");
        }

        return 0;
    }

    /// Lists base-game weapon and enchantment records whose editor id matches a substring, so the real
    /// records the staves build against can be found rather than guessed.
    public static int FindRecords(string filter)
    {
        using var env = GameEnvironment.Typical.Skyrim(SkyrimRelease.SkyrimSE);

        bool Base(FormKey key) => BaseGame.Contains(key.ModKey.ToString());
        bool Match(string? id) => id?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

        Console.WriteLine("== WEAP ==");
        foreach (var w in env.LoadOrder.PriorityOrder.Weapon().WinningOverrides())
            if (Base(w.FormKey) && Match(w.EditorID))
                Console.WriteLine($"  {w.EditorID,-30} anim={w.Data?.AnimationType} model={w.Model?.File.GivenPath}");

        Console.WriteLine("== ObjectEffect ==");
        foreach (var e in env.LoadOrder.PriorityOrder.ObjectEffect().WinningOverrides())
            if (Base(e.FormKey) && Match(e.EditorID))
                Console.WriteLine($"  {e.EditorID}");

        return 0;
    }

    /// Reads a produced plugin back and reports what it holds: record counts, its masters, and the
    /// stats of the weapons (damage, value, weight, reach, speed), so the balance can be checked
    /// without a game. An optional substring narrows the listing to one material or type.
    public static int Inspect(string espPath, string? filter = null)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);

        var weapons = mod.Weapons.ToList();
        var statics = mod.Statics.ToList();
        var recipes = mod.ConstructibleObjects.ToList();

        int forge = recipes.Count(c => c.WorkbenchKeyword.FormKey.ID == CraftingSmithingForge);
        int grind = recipes.Count(c => c.WorkbenchKeyword.FormKey.ID == SharpeningWheel);
        int staves = weapons.Count(w => w.Data?.AnimationType == WeaponAnimationType.Staff);
        Console.WriteLine($"{Path.GetFileName(espPath)}");
        Console.WriteLine($"  weapons {weapons.Count} (staves {staves})   statics {statics.Count}   recipes {recipes.Count} (forge {forge}, grindstone {grind})");
        Console.WriteLine($"  ESL-flagged: {(mod.IsSmallMaster ? "yes" : "no")}");
        Console.WriteLine($"  masters: {string.Join(", ", mod.MasterReferences.Select(m => m.Master.FileName))}");

        int noModel = weapons.Count(w => w.Model?.File.GivenPath is not { } p || !p.StartsWith("obr2sse", StringComparison.OrdinalIgnoreCase));
        int noFp = weapons.Count(w => w.FirstPersonModel.IsNull);
        Console.WriteLine($"  world model not under obr2sse: {noModel}   no first-person link: {noFp}");

        var enchanted = weapons.Where(w => !w.ObjectEffect.IsNull).ToList();
        Console.WriteLine($"  enchanted weapons: {enchanted.Count}");
        foreach (var w in enchanted.OrderBy(w => w.Name?.String, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"    {w.Name?.String,-22} charge {w.EnchantmentAmount,5}  ench {w.ObjectEffect.FormKey}");

        var described = weapons.Where(w => !string.IsNullOrEmpty(w.Description?.String)).ToList();
        Console.WriteLine($"  weapons with a description: {described.Count}");
        foreach (var w in described.OrderBy(w => w.Name?.String, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"    {w.Name?.String,-22} \"{w.Description?.String}\"");

        var shown = weapons
            .Where(w => filter is null || (w.EditorID?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(w => w.EditorID, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"  {"name",-26} {"dmg",4} {"value",6} {"weight",7} {"reach",6} {"speed",6}");
        foreach (var w in shown)
        {
            Console.WriteLine($"  {w.Name?.String,-26} {w.BasicStats?.Damage,4} {w.BasicStats?.Value,6} " +
                              $"{w.BasicStats?.Weight,7:F1} {w.Data?.Reach,6:F2} {w.Data?.Speed,6:F2}");
        }

        return 0;
    }
}
