using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace Obr2Sse;

/// Converts one weapon: geometry and textures out of Oblivion, into a copy of the Skyrim template.
///
/// Which shape of the template gets what is decided by ShapeRoles, not by the shape's position in
/// the file. A template holds the weapon, sometimes a sheath, and effect geometry that has to be
/// left alone, and only the names tell them apart.
public sealed class Pipeline(ObrData obr, SkyrimData skyrim, string outputDir)
{
    /// Everything we write sits under this, so nothing can land on a vanilla file.
    private static readonly string TextureRoot = Path.Combine("textures", "obr2sse");

    private List<string>? _obrMeshes;

    public bool FlipV { get; set; }

    /// Scales each imported piece onto the length of the template it replaces. On by default: the two
    /// games share a unit but not their proportions, and an Oblivion sword is noticeably longer than
    /// the Skyrim one it replaces, so left as authored it stands too far out of the hand.
    public bool Fit { get; set; } = true;

    /// Prints each template's length before and after, to see which weapons actually differ in size.
    public bool Report { get; set; }

    /// One asset's imported geometry and the texture set that goes with it.
    private sealed record Source(List<MeshPrimitive> Parts, string? Stem);

    /// A set of shapes replaced together, and the box they fill before and after. A blood decal only
    /// means anything in one space, so it rides whichever set shares its world position.
    private sealed class Group
    {
        private Group(List<int> shapes, (float[] Min, float[] Max, float[] Size) before)
        {
            Shapes = shapes;
            Before = before;
            After = before;
            Along = LongestAxis(before.Size);
        }

        public List<int> Shapes { get; }

        public (float[] Min, float[] Max, float[] Size) Before { get; }

        public (float[] Min, float[] Max, float[] Size) After { get; set; }

        /// The axis the shapes run longest along, which on a weapon is its length. A decal follows
        /// that axis proportionally and the two across it locally, so which is which is measured
        /// rather than assumed: nothing says a template models its weapon along y.
        public int Along { get; }

        public static Group Of(Nif nif, List<int> shapes)
        {
            var coherent = Coherent(nif, shapes);
            return new Group(coherent, LocalUnion(nif, coherent));
        }
    }

    /// A blood decal, its own box, and what the geometry under it measured across the stretch it
    /// covers. All taken before that geometry is overwritten, since afterwards there is nothing
    /// left to measure.
    private sealed record Decal(int Shape,
                                Group Group,
                                (float[] Min, float[] Max, float[] Size) Box,
                                (float[] Min, float[] Max, float[] Size)? Slab,
                                (float Lo, float Hi, bool TipAtMax)? Blade);

    /// Replacer conversion: the imported weapon overwrites the vanilla mesh at its own path, and its
    /// textures mirror that path under textures\obr2sse. Installed on its own, this changes how every
    /// instance of that vanilla weapon looks.
    public string? Convert(string weaponName, string templatePath)
    {
        // Textures mirror the mesh path: a weapon under meshes\weapons\glass gets its textures under
        // textures\obr2sse\weapons\glass, so the layout stays readable.
        var textures = TextureFolder(templatePath);
        return Convert(weaponName, templatePath, textures, v => Path.Combine(outputDir, v));
    }

    /// Standalone conversion: the imported weapon is injected into a clean per-type steel skeleton and
    /// written to its own path under meshes\obr2sse, leaving every vanilla file untouched. The ESP
    /// then adds a new record pointing here. Textures are keyed on the source, since many weapons
    /// share the one skeleton and a folder keyed on it would collide.
    public string? ConvertStandalone(string weaponName, WeaponType type, string skeletonTemplate)
    {
        var textures = WeaponCatalog.TextureFolder(weaponName, type);

        string OutputFor(string variant)
        {
            bool firstPerson = Path.GetFileName(variant)
                .StartsWith("1stperson", StringComparison.OrdinalIgnoreCase);
            var target = firstPerson
                ? WeaponCatalog.FirstPersonMesh(weaponName, type)
                : WeaponCatalog.WorldMesh(weaponName, type);
            return Path.Combine(outputDir, target);
        }

        return Convert(weaponName, skeletonTemplate, textures, OutputFor);
    }

    /// The shared core: reads the Oblivion geometry once, then writes it into each template variant
    /// (third and first person) at wherever the caller places the output.
    private string? Convert(string weaponName, string templatePath, string textures,
                            Func<string, string> outputFor)
    {
        var weapon = Load(weaponName, textures);
        if (weapon is null)
            return $"no usable mesh named {weaponName}";

        // A staff is nearly symmetric about its length, so the bounding box carries no signal for
        // which end is the head. ToSkyrim negates the length axis, which lands most staves pointing
        // down, so they are turned end for end; a few are authored the right way up and are left alone.
        bool isStaff = WeaponCatalog.Classify(weaponName) == WeaponType.Staff;
        bool flipLength = isStaff && WeaponCatalog.StaffNeedsFlip(weaponName);

        // Skyrim bundles the sheath into the weapon's own NIF; Oblivion ships it as its own asset.
        // Optional on either side.
        var scabbard = Load(weaponName + "_Scabbard", textures);

        string? firstError = null;
        int written = 0;

        foreach (var template in Templates(templatePath))
        {
            if (skyrim.Describe(template) == "not found")
                continue;

            var error = ConvertOne(template, textures, weapon, scabbard, outputFor(template), flipLength, isStaff);
            if (error is null)
            {
                written++;
                continue;
            }

            firstError ??= error;

            // A weapon usually has several meshes and only needs one of them to convert, so a
            // failure here does not fail the weapon. Say so rather than let it pass unseen.
            if (Report)
                Console.WriteLine($"      skipped {error}");
        }

        if (written == 0)
            return firstError ?? "no template found";

        return null;
    }

    /// Where a template's textures go, mirroring its own place under meshes.
    private static string TextureFolder(string templatePath)
    {
        var directory = Path.GetDirectoryName(templatePath.Replace('/', '\\')) ?? string.Empty;

        const string meshes = "meshes\\";
        if (directory.StartsWith(meshes, StringComparison.OrdinalIgnoreCase))
            directory = directory[meshes.Length..];
        else if (directory.Equals("meshes", StringComparison.OrdinalIgnoreCase))
            directory = string.Empty;

        return Path.Combine(TextureRoot, directory);
    }

    /// The first person mesh that goes with a template.
    ///
    /// The base game prefixes the plain name, so ironmace becomes 1stpersonironmace. Creation Club
    /// content names the pair instead, 3rdpersonambersword against 1stpersonambersword, so there
    /// the word is swapped rather than prepended.
    public static string FirstPerson(string templatePath)
    {
        const string third = "3rdperson";

        var directory = Path.GetDirectoryName(templatePath)!;
        var fileName = Path.GetFileName(templatePath);

        return fileName.StartsWith(third, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(directory, "1stperson" + fileName[third.Length..])
            : Path.Combine(directory, "1stperson" + fileName);
    }

    /// The meshes a weapon is spread over: the one held in hand and its first person twin. Absent
    /// ones are dropped by the caller.
    private static IEnumerable<string> Templates(string templatePath)
    {
        yield return templatePath;
        yield return FirstPerson(templatePath);
    }

    /// Reads one Oblivion asset and writes its textures. Null when the asset is absent or empty,
    /// which for a sheath is an ordinary outcome rather than a failure.
    private Source? Load(string assetName, string textures)
    {
        _obrMeshes ??= obr.StandaloneAssets().ToList();

        var meshPath = _obrMeshes.FirstOrDefault(p =>
            Path.GetFileName(p).Equals(assetName, StringComparison.OrdinalIgnoreCase));

        if (meshPath is null)
            return null;

        var mesh = obr.LoadStaticMesh(meshPath);
        if (mesh is null)
            return null;

        var parts = ObrMesh.Load(mesh, assetName);
        return parts.Count == 0 ? null : new Source(parts, WriteTextures(mesh, textures));
    }

    private string? ConvertOne(string template, string textures, Source weapon, Source? scabbard, string meshOut,
                               bool flipLength = false, bool isStaff = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(meshOut)!);

        var scratch = Path.Combine(outputDir, Path.GetRandomFileName() + ".nif");
        File.WriteAllBytes(scratch, skyrim.Read(template));

        try
        {
            using var nif = new Nif(scratch);

            var targets = ShapeRoles.Find(nif, ShapeRole.Weapon);

            if (targets.Count == 0)
                return $"{Path.GetFileName(template)}: no weapon shape";

            // Replacing the vertices of a skinned shape throws its bone weights away, so those
            // templates are left alone rather than written out broken.
            if (targets.Any(nif.IsSkinned))
                return $"{Path.GetFileName(template)}: skinned shape, not supported";

            // Every role is read before anything is written. A shape is classified partly by the
            // textures it carries, and Paint changes those.
            var sheaths = ShapeRoles.Find(nif, ShapeRole.Scabbard);

            // Taken over every weapon shape, and taken now, before any of them are dropped below. A
            // template that splits its weapon in two only gives its real length with both halves
            // counted: measured on the hilt alone it would shrink the whole import onto the hilt.
            var templateBounds = TemplateBounds(nif, targets);

            float templateSpan = templateBounds.Max.Y - templateBounds.Min.Y;

            int weaponShapes = targets.Count;

            // A template with fewer shapes than the weapon has material sections takes the lot in
            // one: the split is Oblivion's, and Skyrim does not need to keep it.
            var parts = targets.Count == 1
                ? new List<MeshPrimitive> { MeshPrimitive.Merge(weapon.Parts) }
                : weapon.Parts;

            // The counts can still disagree the other way round, more shapes in the template than
            // sections in the source. Pairing them off in file order would put a hilt where a blade
            // belongs, so the whole weapon goes into whichever shape is proportioned most like it. The
            // others held the rest of the vanilla weapon, which the imported one now replaces whole, so
            // they are dropped rather than left showing vanilla geometry the import overlaps.
            var unusedWeaponShapes = new List<int>();
            if (targets.Count != parts.Count)
            {
                var source = Converter.SkyrimBounds(weapon.Parts);
                int best = targets[0];
                float bestScore = float.MaxValue;

                foreach (var shape in targets)
                {
                    float score = ShapeDistance(source, ShapeBounds(nif, shape));
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = shape;
                    }
                }

                unusedWeaponShapes = targets.Where(t => t != best).ToList();
                targets = new List<int> { best };
                parts = new List<MeshPrimitive> { MeshPrimitive.Merge(weapon.Parts) };
            }

            // Oblivion does not always model a weapon along the same axis as Skyrim, so the source is
            // turned onto the template's axis before anything is measured against it.
            var alignment = Converter.AlignAxes(Converter.SkyrimBounds(parts), templateBounds);
            var rotation = Fit ? FitLength(alignment, parts, templateSpan) : alignment;

            // Turn a staff end for end about the length axis, so its head sits up where the box could
            // not tell the converter to put it. A half turn is its own inverse, so this simply undoes
            // the reversal ToSkyrim introduced. Rounded about its shaft, a staff shows nothing of the
            // extra roll the turn carries with it.
            if (flipLength)
                rotation *= Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

            var offset = Offset(isStaff);

            // Everything a blood decal can be hung on, weapon first: a decal sharing its node with
            // more than one of these belongs to the weapon rather than to whatever else happens to
            // hang off the same node. Only geometry that is actually replaced is listed - a decal
            // over a piece left vanilla has nothing to follow and is better left alone.
            var groups = new List<Group> { Group.Of(nif, targets) };

            if (scabbard is not null && sheaths.Count > 0)
                groups.Add(Group.Of(nif, sheaths));

            var decals = CollectDecals(nif, groups);

            for (int i = 0; i < targets.Count; i++)
            {
                Converter.Inject(nif, targets[i], parts[i], FlipV, rotation, offset);

                // Oblivion models blades as thin open shells - two faces, no closing rim - fine under
                // UE5's two-sided material but see-through once Skyrim culls back faces. Drawing both
                // sides (SLSF2_DOUBLE_SIDED) is the engine's own fix, and free on solid parts.
                nif.SetDoubleSided(targets[i]);
            }

            Paint(nif, targets, textures, weapon.Stem);

            // The sheath wraps the blade, so it takes the weapon's own rotation and offset. Measured
            // against its vanilla shape instead, it would be free to turn a different way and come
            // out crossing the blade it is meant to hold.
            if (scabbard is not null && sheaths.Count > 0)
            {
                var merged = MeshPrimitive.Merge(scabbard.Parts);
                foreach (var sheath in sheaths)
                    Converter.Inject(nif, sheath, merged, FlipV, rotation, offset);

                Paint(nif, sheaths, textures, scabbard.Stem);
            }

            foreach (var group in groups)
                group.After = LocalUnion(nif, group.Shapes);

            float convertedSpan = 0f;
            foreach (var shape in targets)
                convertedSpan = Math.Max(convertedSpan, nif.Bounds(shape).Size[1]);

            if (Report)
            {
                var turned = alignment.IsIdentity ? "" : "  turned";

                // Says so when several weapon shapes were folded into one, since the ones left
                // behind keep their vanilla geometry and that is worth seeing.
                var merged = targets.Count < weaponShapes ? $"  merged {weaponShapes} into 1" : "";

                Console.WriteLine($"      {Path.GetFileName(template),-34} " +
                                  $"template {templateSpan,7:F1}  converted {convertedSpan,7:F1}  " +
                                  $"offset {offset.Y,6:F1}  " +
                                  $"{weaponShapes}w {sheaths.Count}s{turned}{merged}");
            }

            foreach (var decal in decals)
                Lay(nif, decal);

            // Vanilla glow and effect overlays are cut to the vanilla silhouette and hang off the
            // imported mesh as stray light; Oblivion bakes its glow into the texture, so drop them.
            // Highest index first, since deleting a shape shifts the ones above it.
            var overlays = ShapeRoles.Find(nif, ShapeRole.Glow)
                .Concat(ShapeRoles.Find(nif, ShapeRole.Effect))
                .Concat(unusedWeaponShapes)
                .Distinct()
                .OrderByDescending(i => i);
            foreach (var overlay in overlays)
                nif.DeleteShape(overlay);

            nif.Save(meshOut, optimize: true, sortBlocks: true);
            return null;
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    /// Every blood decal, paired with the geometry it lies on and measured while that geometry is
    /// still the vanilla one. Afterwards there is nothing left to compare against.
    private static List<Decal> CollectDecals(Nif nif, List<Group> groups)
    {
        var decals = new List<Decal>();

        foreach (var shape in ShapeRoles.Find(nif, ShapeRole.Blood))
        {
            // A decal often sits in its own node, so match it to a replaced group by world position
            // and rewrite it into that group's frame; the steps below need it in local space.
            var group = NearestGroup(nif, shape, groups);
            if (group is null)
                continue;

            nif.Reframe(shape, group.Shapes[0]);

            var box = nif.LocalBounds(shape);
            var slab = SliceUnion(nif, group.Shapes, group.Along, box.Min[group.Along], box.Max[group.Along]);

            // Where the blade runs, against the vanilla weapon. The tip is the end the decal reaches
            // closest to; null when no crossguard stands out, and then the whole box carries it.
            int a = group.Along;
            bool tipAtMax = group.Before.Max[a] - box.Max[a] <= box.Min[a] - group.Before.Min[a];
            var blade = BladeSpan(nif, group.Shapes, a,
                                  group.Before.Min[a], group.Before.Max[a], tipAtMax);

            decals.Add(new Decal(shape, group, box, slab,
                                 blade is { } span ? (span.Lo, span.Hi, tipAtMax) : null));
        }

        return decals;
    }

    /// Puts a decal back on the geometry that replaced what it was drawn on. Three steps, all needed:
    /// remap places the strip roughly right, subdivide gives it vertices to bend, projection lays it
    /// on the metal.
    private static void Lay(Nif nif, Decal decal)
    {
        Follow(nif, decal);

        // Projection only moves vertices that already exist, so subdivide first or a coarse strip
        // bridges the surface between its corners.
        var box = nif.LocalBounds(decal.Shape);
        float span = MathF.Max(box.Size[0], MathF.Max(box.Size[1], box.Size[2]));

        // Finer than ten segments across, so the strip follows a curved head (a mace) instead of
        // bridging it as a flat chord. A blade is flat, so the extra segments cost only a few vertices.
        nif.Subdivide(decal.Shape, MathF.Max(0.5f, span / 16f));

        // Sized on the blade thickness under the decal, not the whole weapon: a sword's box is set by
        // its crossguard, and a lift scaled to that would hold the blood off a thin blade.
        float thickness = BladeThickness(nif, decal.Group, box);

        // Lift stays small and capped so a bulky head (a mace) doesn't hold the blood off; the limit
        // scales with thickness, so a vertex on the wrong side of a thick head is still pulled back.
        nif.Project(decal.Shape, decal.Group.Shapes.ToArray(),
                    lift: MathF.Min(0.15f, MathF.Max(0.05f, 0.03f * thickness)),
                    limit: MathF.Max(1f, thickness));
    }

    /// Blade thickness under the decal: the thinner cross-section extent over the stretch it covers,
    /// measured on the geometry there so the crossguard never sets it. Falls back to the whole group.
    private static float BladeThickness(Nif nif, Group group,
                                        (float[] Min, float[] Max, float[] Size) box)
    {
        int along = group.Along;

        var slab = SliceUnion(nif, group.Shapes, along, box.Min[along], box.Max[along])
                   ?? group.After;

        float thickness = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            if (axis != along)
                thickness = MathF.Min(thickness, slab.Size[axis]);
        }

        return thickness;
    }

    /// Folds a uniform scale into a rotation, so the source ends up as long as the template.
    ///
    /// Uniform, so the piece keeps its proportions, and about the origin rather than about its own
    /// centre: on a weapon the origin is where the grip sits, and that is the one point that has to
    /// stay put.
    private static Matrix4x4 FitLength(Matrix4x4 rotation, List<MeshPrimitive> parts, float targetSpan)
    {
        var bounds = Converter.SkyrimBounds(parts, rotation);
        float span = bounds.Max.Y - bounds.Min.Y;

        if (span <= 1e-3f || targetSpan <= 0f)
            return rotation;

        return rotation * Matrix4x4.CreateScale(targetSpan / span);
    }

    /// How far a staff is raised along its length, so its head sits nearer the Skyrim spell effect,
    /// which stays at the vanilla staff's tip rather than following the shorter OBR head.
    private const float StaffLift = 10f;

    /// Where the imported weapon sits along the template's length. Weapons keep their own origin: both
    /// games put it at the grip, so an imported weapon dropped in at its own origin already lands in the
    /// hand, within a couple of units on every weapon tried (a greatsword, a dagger, a mace). Staves are
    /// nudged up the length so the head meets the enchantment glow the game leaves at the vanilla tip.
    private static Vector3 Offset(bool isStaff) => isStaff ? new Vector3(0f, StaffLift, 0f) : Vector3.Zero;

    /// The shapes of a set placed by the same node as the first. Only those can be measured against
    /// each other: a local coordinate means nothing outside its own frame.
    private static List<int> Coherent(Nif nif, List<int> shapes) =>
        shapes.Where(shape => shape == shapes[0] || SameFrame(nif, shape, shapes[0])).ToList();

    /// The axis a box is longest along.
    private static int LongestAxis(float[] size) =>
        size[0] >= size[1] && size[0] >= size[2] ? 0
            : size[1] >= size[2] ? 1
            : 2;

    /// The local box of several shapes taken together, skipping any placed by a different node
    /// since its coordinates would describe another space entirely.
    private static (float[] Min, float[] Max, float[] Size) LocalUnion(Nif nif, List<int> shapes)
    {
        var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new[] { float.MinValue, float.MinValue, float.MinValue };

        foreach (var shape in shapes)
        {
            if (shape != shapes[0] && !SameFrame(nif, shape, shapes[0]))
                continue;

            var box = nif.LocalBounds(shape);

            for (int axis = 0; axis < 3; axis++)
            {
                min[axis] = MathF.Min(min[axis], box.Min[axis]);
                max[axis] = MathF.Max(max[axis], box.Max[axis]);
            }
        }

        var size = new float[3];
        for (int axis = 0; axis < 3; axis++)
            size[axis] = max[axis] - min[axis];

        return (min, max, size);
    }

    /// The replaced group a decal lies on, by world position: the one whose world box its own world
    /// centre sits in, or nearest to. Null when the nearest group is still further off than that
    /// group is large, so a decal over something we never touched is left exactly as it was rather
    /// than dragged onto an unrelated piece.
    private static Group? NearestGroup(Nif nif, int shape, List<Group> groups)
    {
        var (lo, hi, _) = nif.Bounds(shape);
        var centre = new Vector3((lo[0] + hi[0]) / 2f, (lo[1] + hi[1]) / 2f, (lo[2] + hi[2]) / 2f);

        Group? nearest = null;
        float best = float.MaxValue;
        float reach = 0f;

        foreach (var group in groups)
        {
            var box = TemplateBounds(nif, group.Shapes);
            float distance = BoxDistance(box, centre);

            if (distance < best)
            {
                best = distance;
                nearest = group;
                reach = (box.Max - box.Min).Length();
            }
        }

        return best <= reach ? nearest : null;
    }

    /// Distance from a point to an axis-aligned box, zero when the point is inside it.
    private static float BoxDistance((Vector3 Min, Vector3 Max) box, Vector3 p)
    {
        float dx = MathF.Max(0f, MathF.Max(box.Min.X - p.X, p.X - box.Max.X));
        float dy = MathF.Max(0f, MathF.Max(box.Min.Y - p.Y, p.Y - box.Max.Y));
        float dz = MathF.Max(0f, MathF.Max(box.Min.Z - p.Z, p.Z - box.Max.Z));

        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// Whether two shapes are placed by the same chain of nodes, which is what makes their local
    /// coordinates mean the same thing.
    private static bool SameFrame(Nif nif, int a, int b)
    {
        var first = nif.WorldTransform(a);
        var second = nif.WorldTransform(b);

        for (int i = 0; i < 3; i++)
        {
            if (MathF.Abs(first.Translation[i] - second.Translation[i]) > 1e-3f)
                return false;
        }

        for (int i = 0; i < 9; i++)
        {
            if (MathF.Abs(first.Rotation[i] - second.Rotation[i]) > 1e-3f)
                return false;
        }

        return MathF.Abs(first.Scale - second.Scale) < 1e-3f;
    }

    /// Moves a decal from the geometry it was drawn on to the geometry that replaced it. Along the
    /// length it keeps its place as a fraction of the blade; across the length it follows the blade's
    /// own cross-section, not the whole box, which a crossguard would otherwise skew.
    private static void Follow(Nif nif, Decal decal)
    {
        var group = decal.Group;
        int along = group.Along;

        var vanilla = group.Before;
        var imported = group.After;

        // Place along the length as a fraction of the blade, not the whole weapon, since the crossguard
        // is a different share of the length in each game. Only when a guard stands out on both.
        float anchorLo = vanilla.Min[along];
        float anchorSize = vanilla.Size[along];
        float placedLo = imported.Min[along];
        float placedSize = imported.Size[along];

        if (decal.Blade is { } blade &&
            BladeSpan(nif, group.Shapes, along,
                      imported.Min[along], imported.Max[along], blade.TipAtMax) is { } placed)
        {
            anchorLo = blade.Lo;
            anchorSize = blade.Hi - blade.Lo;
            placedLo = placed.Lo;
            placedSize = placed.Hi - placed.Lo;
        }

        float ratio = anchorSize > 1e-3f ? placedSize / anchorSize : 1f;

        float low = placedLo + (decal.Box.Min[along] - anchorLo) * ratio;
        float high = placedLo + (decal.Box.Max[along] - anchorLo) * ratio;

        var scale = new[] { 1f, 1f, 1f };
        var from = new float[3];
        var to = new float[3];

        scale[along] = ratio;
        from[along] = (decal.Box.Min[along] + decal.Box.Max[along]) / 2f;
        to[along] = (low + high) / 2f;

        var after = SliceUnion(nif, group.Shapes, along, low, high);

        for (int axis = 0; axis < 3; axis++)
        {
            if (axis == along)
                continue;

            // Without a usable slab on either side there is nothing local to go on, so the whole
            // box has to do. It is the weaker answer, not a wrong one.
            if (decal.Slab is not { } before || after is not { } now || before.Size[axis] <= 1e-3f)
            {
                scale[axis] = vanilla.Size[axis] > 1e-3f ? imported.Size[axis] / vanilla.Size[axis] : 1f;
                from[axis] = (vanilla.Min[axis] + vanilla.Max[axis]) / 2f;
                to[axis] = (imported.Min[axis] + imported.Max[axis]) / 2f;
                continue;
            }

            scale[axis] = now.Size[axis] / before.Size[axis];
            from[axis] = (before.Min[axis] + before.Max[axis]) / 2f;
            to[axis] = (now.Min[axis] + now.Max[axis]) / 2f;
        }

        nif.Remap(decal.Shape, scale[0], scale[1], scale[2],
                  from[0], from[1], from[2], to[0], to[1], to[2]);
    }

    /// The cross-section of several shapes over one stretch of their length, taken together.
    private static (float[] Min, float[] Max, float[] Size)? SliceUnion(Nif nif, List<int> shapes,
                                                                        int axis, float low, float high)
    {
        var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new[] { float.MinValue, float.MinValue, float.MinValue };
        bool found = false;

        foreach (var shape in shapes)
        {
            if (nif.SliceBounds(shape, axis, low, high) is not { } slab)
                continue;

            found = true;

            for (int i = 0; i < 3; i++)
            {
                min[i] = MathF.Min(min[i], slab.Min[i]);
                max[i] = MathF.Max(max[i], slab.Max[i]);
            }
        }

        if (!found)
            return null;

        var size = new float[3];
        for (int i = 0; i < 3; i++)
            size[i] = max[i] - min[i];

        return (min, max, size);
    }

    /// How finely the length is sampled looking for the crossguard, and how much wider than the
    /// blade a slice has to be to read as the guard rather than as the blade itself.
    private const int BladeBins = 24;
    private const float GuardWidthFactor = 1.7f;

    /// The blade's longitudinal span, crossguard to tip. The blade runs most of the length so its
    /// cross-section sets the median and the guard stands above it. Sampled coarsely where vertices
    /// fall; null when no guard stands out (a dagger or a mace), and the caller uses the whole box.
    private static (float Lo, float Hi)? BladeSpan(
        Nif nif, List<int> shapes, int along, float min, float max, bool tipAtMax)
    {
        float span = max - min;
        if (span <= 1e-3f)
            return null;

        float step = span / BladeBins;
        int a1 = (along + 1) % 3;
        int a2 = (along + 2) % 3;

        var width = new float[BladeBins];
        var present = new bool[BladeBins];
        var widths = new List<float>();

        for (int i = 0; i < BladeBins; i++)
        {
            float lo = min + i * step;
            float hi = i == BladeBins - 1 ? max : lo + step;

            if (SliceUnion(nif, shapes, along, lo, hi) is not { } slab)
                continue;

            present[i] = true;
            width[i] = MathF.Max(slab.Size[a1], slab.Size[a2]);
            widths.Add(width[i]);
        }

        if (widths.Count == 0)
            return null;

        widths.Sort();
        float threshold = widths[widths.Count / 2] * GuardWidthFactor;
        float mid = (min + max) / 2f;

        // Grip, guard and pommel all sit on the grip side, and both guard and pommel stand out above
        // the blade's width. The blade begins at the top of the guard, so of the wide slices on the
        // grip side the one nearest the blade is the guard, and its inner edge is where the blade
        // starts. The pommel, further out at the very end of the grip, is passed over.
        int guard = -1;
        for (int i = 0; i < BladeBins; i++)
        {
            if (!present[i] || width[i] <= threshold)
                continue;

            float centre = min + (i + 0.5f) * step;
            bool gripSide = tipAtMax ? centre < mid : centre > mid;
            if (!gripSide)
                continue;

            // The guard slice nearest the blade: highest towards a tip at the top, lowest towards a
            // tip at the bottom.
            if (guard < 0 ||
                (tipAtMax ? i > guard : i < guard))
                guard = i;
        }

        if (guard < 0)
            return null;

        // The tip end keeps the exact extreme; the blade begins at the guard slice's inner edge.
        return tipAtMax
            ? (min + (guard + 1) * step, max)
            : (min, min + guard * step);
    }

    /// World-space bounds of one shape.
    private static (Vector3 Min, Vector3 Max) ShapeBounds(Nif nif, int shape)
    {
        var (lo, hi, _) = nif.Bounds(shape);
        return (new Vector3(lo[0], lo[1], lo[2]), new Vector3(hi[0], hi[1], hi[2]));
    }

    /// How differently two boxes are proportioned, ignoring their size and where they sit.
    private static float ShapeDistance((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b)
    {
        var first = Proportions(a);
        var second = Proportions(b);

        return MathF.Abs(first.X - second.X) + MathF.Abs(first.Y - second.Y);
    }

    /// Width and depth of a box relative to its own height.
    private static Vector2 Proportions((Vector3 Min, Vector3 Max) bounds)
    {
        var size = bounds.Max - bounds.Min;
        float height = MathF.Max(size.Z, 1e-3f);

        return new Vector2(size.X / height, size.Y / height);
    }

    /// World-space bounds of a set of shapes, taken together.
    private static (Vector3 Min, Vector3 Max) TemplateBounds(Nif nif, List<int> shapes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var shape in shapes)
        {
            var (lo, hi, _) = nif.Bounds(shape);
            min = Vector3.Min(min, new Vector3(lo[0], lo[1], lo[2]));
            max = Vector3.Max(max, new Vector3(hi[0], hi[1], hi[2]));
        }

        return (min, max);
    }

    /// Points a set of shapes at a texture set of ours. Each shape is given its own block first:
    /// vanilla templates share one across several shapes, so writing a slot in place would repaint
    /// shapes we never touched.
    private static void Paint(Nif nif, List<int> shapes, string textures, string? stem)
    {
        if (stem is null)
            return;

        foreach (var shape in shapes)
        {
            nif.DetachTextures(shape);
            nif.SetTexture(shape, 0, Path.Combine(textures, $"{stem}.dds"));
            nif.SetTexture(shape, 1, Path.Combine(textures, $"{stem}_n.dds"));
            nif.SetTexture(shape, 5, Path.Combine(textures, $"{stem}_m.dds"));
        }
    }

    /// Writes the texture set and returns its base name, or null if the material has no usable pair.
    /// The material is what knows which textures it uses: a weapon folder holds every weapon of that
    /// material, so resolving by folder would pick an arbitrary one.
    private string? WriteTextures(UStaticMesh mesh, string textures)
    {
        // The first material is usually the weapon's own, but not always: a daedric mace leads with
        // its glow, which carries no base colour. So every material is tried until one gives both the
        // albedo and the packed map.
        //
        // A weapon's decorative sections name their diffuse with a suffix on the weapon's own name - a
        // sword mounted in stone leads with T_..._Stone, a gem with T_..._Gem. When one of those comes
        // first it must not be taken as the weapon's texture, so a suffixed candidate is only kept as a
        // fallback and a plainer material later in the list wins. Weapons with a single material, which
        // is all but a handful, are unaffected.
        UTexture2D? albedo = null;
        UTexture2D? packed = null;
        UTexture2D? fallbackAlbedo = null;
        UTexture2D? fallbackPacked = null;

        int materials = mesh.StaticMaterials?.Length ?? 1;
        for (int m = 0; m < materials; m++)
        {
            var parameters = obr.MaterialTextures(ObrMesh.Material(mesh, m));
            var a = Pick(parameters, "BaseColor Map", "PM_Diffuse", "Diffuse");
            var p = Pick(parameters, "NNRM Map", "NNR Map", "PM_SpecularMasks");

            if (a is null || p is null)
                continue;

            if (IsDecorativeMaterial(a.Name))
            {
                fallbackAlbedo ??= a;
                fallbackPacked ??= p;
                continue;
            }

            albedo = a;
            packed = p;
            break;
        }

        albedo ??= fallbackAlbedo;
        packed ??= fallbackPacked;

        if (albedo is null || packed is null)
            return null;

        var stem = albedo.Name.EndsWith("_D", StringComparison.OrdinalIgnoreCase)
            ? albedo.Name[..^2]
            : albedo.Name;

        // Unreal orders its mips largest first and GetFirstMip returns the first one whose data can
        // actually be read, so what we decode is already the best available. PlatformData carries
        // the authored size regardless: when the two disagree, the top level lives in a bulk file
        // that is not mounted and the output really is losing resolution.
        if (Report)
        {
            var mip = albedo.GetFirstMip();
            int authoredX = albedo.PlatformData.SizeX;
            int authoredY = albedo.PlatformData.SizeY;
            int gotX = mip?.SizeX ?? 0;
            int gotY = mip?.SizeY ?? 0;

            var lost = gotX < authoredX || gotY < authoredY ? "  TOP MIP MISSING" : "";

            Console.WriteLine($"      {stem,-34} authored {authoredX}x{authoredY}  " +
                              $"decoded {gotX}x{gotY}  {albedo.Format}{lost}");
        }

        var folder = Path.Combine(outputDir, textures);
        Directory.CreateDirectory(folder);

        var diffuseFile = Path.Combine(folder, $"{stem}.dds");
        var normalFile = Path.Combine(folder, $"{stem}_n.dds");
        var maskFile = Path.Combine(folder, $"{stem}_m.dds");

        // Weapons of the same material share an atlas, so this is skipped most of the time.
        if (File.Exists(diffuseFile) && File.Exists(normalFile) && File.Exists(maskFile))
            return stem;

        Textures.WriteDiffuse(albedo, diffuseFile);
        Textures.WriteNormal(packed, normalFile);
        Textures.WriteEnvironmentMask(packed, maskFile);

        return stem;
    }

    /// A diffuse whose name ends in a decorative suffix belongs to a mount or a fitting, not the
    /// weapon: a stone base, a gem, a glow. Kept narrow so it only ever defers a section that names
    /// itself as one, never a plain material.
    private static readonly string[] DecorativeSuffixes =
        { "_Stone", "_Gem", "_Crystal", "_Glow", "_Base", "_Pedestal", "_Rock" };

    private static bool IsDecorativeMaterial(string diffuseName)
    {
        var stem = diffuseName.EndsWith("_D", StringComparison.OrdinalIgnoreCase)
            ? diffuseName[..^2]
            : diffuseName;

        return DecorativeSuffixes.Any(s => stem.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    private static UTexture2D? Pick(Dictionary<string, UTexture2D> textures, params string[] names)
    {
        foreach (var name in names)
        {
            if (textures.TryGetValue(name, out var texture))
                return texture;
        }

        return null;
    }
}
