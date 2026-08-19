using System.Diagnostics;
using System.Numerics;
using SixLabors.ImageSharp;
using Obr2Sse;

// Builds Skyrim weapons and textures from an Oblivion Remastered install, using the vanilla NIFs as
// templates. Neither game's files are redistributed: both are read from the user's own copies.
//
// `convert` is the entry point; the rest are diagnostics. Run with no arguments for the command list.

if (args.Length == 0)
{
    Usage();
    return 1;
}

// Positional arguments, with flags (--x) pulled out so a flag anywhere on the line never shifts the
// meaning of the arguments that follow it.
var pos = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

Textures.HighQuality = args.Contains("--bc7");
var flipV = args.Contains("--flip-v");

// High by default: keep the full Oblivion geometry, thinned only where a shape would blow Skyrim's
// vertex ceiling. The lighter presets trade that detail for a smaller, faster mod.
ObrMesh.Quality = args.Contains("--vanilla") ? ObrMesh.MeshQuality.Vanilla
    : args.Contains("--balanced") ? ObrMesh.MeshQuality.Balanced
    : ObrMesh.MeshQuality.High;

// On by default: the two games share a unit but not their proportions, and an Oblivion weapon is
// noticeably longer than the Skyrim one it stands in for, so without this it comes out oversized.
var fit = !args.Contains("--no-fit");

switch (pos[0])
{
    case "probe" when pos.Length >= 2:
        return ProbeObr.Run(pos[1], pos.Length > 2 ? pos[2] : null,
                            pos.Length > 3 ? pos[3] : null);

    case "propose" when pos.Length >= 5:
        return ProposeMapping.Run(pos[1], pos[2], pos[3], pos[4]);

    // Standalone by default: sweep every OBR weapon into its own mesh + record. With --replacer the
    // fourth argument is the curated replacer mapping; standalone needs only the output directory.
    case "convert" when args.Contains("--replacer") && pos.Length >= 6:
        return Convert(pos[1], pos[2], pos[3], pos[4], pos[5]);

    case "convert" when pos.Length >= 5:
        return Convert(pos[1], pos[2], pos[3], mappingFile: null, pos[4]);

    case "catalog" when pos.Length >= 4:
        return Catalog(pos[1], pos[2], pos[3]);

    case "one" when pos.Length >= 7:
        return One(pos[1], pos[2], pos[3], pos[4], pos[5], pos[6]);

    case "compare" when pos.Length >= 6:
        return Compare(pos[1], pos[2], pos[3], pos[4], pos[5]);

    case "nodes" when pos.Length >= 3:
        return Nodes(pos[1], pos[2]);

    case "records" when pos.Length >= 2:
        return EspBuilder.FindRecords(pos[1]);

    case "espinfo" when pos.Length >= 2:
        return EspBuilder.Inspect(pos[1], pos.Length > 2 ? pos[2] : null);

    case "esp" when pos.Length >= 4:
        return Esp(pos[1], pos[2], pos[3], pos.Length > 4 ? pos[4] : null);

    case "meshcheck" when pos.Length >= 5:
        return MeshCheck(pos[1], pos[2], pos[3], pos[4]);

    case "audit" when pos.Length >= 5:
        return Audit(pos[1], pos[2], pos[3], pos[4]);

    case "render" when pos.Length >= 6:
        return Render(pos[1], pos[2], pos[3], pos[4], pos[5]);

    case "contact" when pos.Length >= 6:
        return Contact(pos[1], pos[2], pos[3], pos[4], pos[5]);

    case "survey" when pos.Length >= 3:
        return Survey(pos[1], pos[2]);

    case "list" when pos.Length >= 2:
        return List(pos[1], pos.Length > 2 ? pos[2] : null);

    case "assets" when pos.Length >= 3:
        return Assets(pos[1], pos[2], pos.Length > 3 ? pos[3] : null);

    default:
        Usage();
        return 1;
}

int Convert(string skyrimPath, string oblivionPath, string mappingsPath, string? mappingFile, string outputDir)
{
    var stopwatch = Stopwatch.StartNew();

    using var obr = new ObrData(oblivionPath, mappingsPath);
    using var skyrim = new SkyrimData(skyrimPath);

    var pipeline = new Pipeline(obr, skyrim, outputDir)
    {
        FlipV = flipV,
        Fit = fit,
        Report = args.Contains("--report"),
    };

    // Replacer overwrites the vanilla meshes named in the curated mapping; standalone (the default)
    // sweeps every OBR weapon into its own mesh and adds an ESP that makes each a new craftable item.
    if (mappingFile is not null)
        return Replacer(pipeline, MappingFile.Load(mappingFile), outputDir, stopwatch);

    return Standalone(pipeline, obr, outputDir, stopwatch, skyrim.DataFolder);
}

int Replacer(Pipeline pipeline, MappingFile mapping, string outputDir, Stopwatch stopwatch)
{
    var result = ReplacerRun.Execute(pipeline, mapping,
        onWeapon: (_, _, source) => Console.WriteLine($"  {source}"));

    Console.WriteLine();
    Console.WriteLine($"{result.Converted} of {result.Total} converted in {stopwatch.Elapsed.TotalSeconds:F1}s (replacer)");
    Report(result.Failures.ToList(), outputDir);
    return 0;
}

int Standalone(Pipeline pipeline, ObrData obr, string outputDir, Stopwatch stopwatch, string skyrimDataFolder)
{
    var result = StandaloneRun.Execute(obr, pipeline, outputDir,
        onWeapon: (_, _, source) => Console.WriteLine($"  {source}"),
        skyrimDataFolder: skyrimDataFolder);

    Console.WriteLine();
    Console.WriteLine($"{result.Converted} of {result.Total} converted in {stopwatch.Elapsed.TotalSeconds:F1}s (standalone)");

    if (result.EspError is not null)
        Console.WriteLine($"ESP not written: {result.EspError}");

    if (result.Unclassified.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{result.Unclassified.Count} out of scope:");
        foreach (var u in result.Unclassified)
            Console.WriteLine($"  {u}");
    }

    Report(result.Failures.ToList(), outputDir);
    return 0;
}

void Report(List<string> failures, string outputDir)
{
    if (failures.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{failures.Count} skipped:");
        foreach (var failure in failures)
            Console.WriteLine($"  {failure}");
    }

    Console.WriteLine();
    Console.WriteLine($"output: {Path.GetFullPath(outputDir)}");
}

// Writes the proposed standalone catalog as JSON so the full sweep - what each OBR weapon was read
// as, the skeleton it builds against, the record it inherits - can be reviewed without converting.
int Catalog(string oblivionPath, string mappingsPath, string outJson)
{
    using var obr = new ObrData(oblivionPath, mappingsPath);
    var (entries, unclassified) = WeaponCatalog.Build(obr.StandaloneAssets());

    var mapping = new MappingFile
    {
        Weapons = entries.Select(e => new WeaponMapping
        {
            Source = e.Source,
            Template = e.Skeleton,
            Note = $"{e.Type}, stats {e.Record}",
        }).ToList(),
    };
    mapping.Save(outJson);

    var byType = entries.GroupBy(e => e.Type).OrderBy(g => g.Key.ToString());
    Console.WriteLine($"{entries.Count} weapons in scope:");
    foreach (var g in byType)
        Console.WriteLine($"  {g.Key,-12} {g.Count()}");
    Console.WriteLine($"uniques with a named base record: {entries.Count(e => e.Record is not null)}");
    if (unclassified.Count > 0)
    {
        Console.WriteLine($"{unclassified.Count} out of scope:");
        foreach (var u in unclassified)
            Console.WriteLine($"  {u}");
    }
    Console.WriteLine($"wrote {outJson}");
    return 0;
}

// Rebuilds only the standalone ESP from the OBR sweep, for when the meshes are already converted.
// Only weapons whose mesh is actually present in the output are included, so the plugin never carries
// a record pointing at a mesh that was not written.
int Esp(string oblivionPath, string mappingsPath, string outputDir, string? skyrimDataFolder)
{
    using var obr = new ObrData(oblivionPath, mappingsPath);
    var (entries, _) = WeaponCatalog.Build(obr.StandaloneAssets());

    var present = entries
        .Where(e => File.Exists(Path.Combine(outputDir, WeaponCatalog.WorldMesh(e.Source, e.Type))))
        .ToList();

    Console.WriteLine($"{present.Count} of {entries.Count} weapons have a converted mesh in {outputDir}");
    return EspBuilder.BuildStandalone(present, Path.Combine(outputDir, "OBR2SSE - Weapons.esp"), skyrimDataFolder);
}

int One(string skyrimPath, string oblivionPath, string mappingsPath, string weapon, string template, string outputDir)
{
    var stopwatch = Stopwatch.StartNew();

    using var obr = new ObrData(oblivionPath, mappingsPath);
    using var skyrim = new SkyrimData(skyrimPath);

    var pipeline = new Pipeline(obr, skyrim, outputDir) { FlipV = flipV, Fit = fit };
    var error = pipeline.Convert(weapon, template);

    if (error is not null)
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    Console.WriteLine($"{weapon} -> {Path.GetFullPath(outputDir)} in {stopwatch.ElapsedMilliseconds} ms");
    return 0;
}

// Geometry health of one merged weapon mesh: isolated vertices (decimation spikes that render as stray
// lines), real holes (open edges), and where along the length those holes sit. Shared by meshcheck (one
// weapon, verbose) and audit (every weapon, ranked), so both report the same numbers the same way.
(int Verts, int Tris, int Isolated1, int Isolated2, float Worst, int Holes, int[] Bins, int Axis)
    AnalyzeMesh(MeshPrimitive merged)
{
    var p = merged.Positions;
    int n = p.Length;

    // The visible stray-line artefact is an isolated vertex: one whose nearest connected neighbour sits
    // far off, joined to the surface only by a needle fan. Measured directly here - thin surface tris
    // (a blade edge is full of them) are fine and not counted.
    var nearest = new float[n];
    Array.Fill(nearest, float.MaxValue);

    void Edge(int a, int b)
    {
        float d = (p[a] - p[b]).Length();
        if (d < nearest[a]) nearest[a] = d;
        if (d < nearest[b]) nearest[b] = d;
    }

    for (int i = 0; i < merged.Indices.Length; i += 3)
    {
        int a = (int)merged.Indices[i], b = (int)merged.Indices[i + 1], c = (int)merged.Indices[i + 2];
        Edge(a, b); Edge(b, c); Edge(c, a);
    }

    int isolated1 = 0, isolated2 = 0;
    float worst = 0f;
    for (int v = 0; v < n; v++)
    {
        if (nearest[v] == float.MaxValue) continue;
        if (nearest[v] > 1.0f) isolated1++;
        if (nearest[v] > 2.0f) isolated2++;
        if (nearest[v] > worst) worst = nearest[v];
    }

    // True holes, measured by POSITION not index: an edge shared by an odd number of triangles is a
    // real open edge. A UV seam splits the index topology but both sides sit at the same position, so
    // it counts even and does not show here - only genuine cracks (a notch bitten out of the blade) do.
    (long, long, long) Q(Vector3 v) =>
        ((long)MathF.Round(v.X * 50f), (long)MathF.Round(v.Y * 50f), (long)MathF.Round(v.Z * 50f));

    var posEdge = new Dictionary<((long, long, long), (long, long, long)), int>();
    void CountPos(Vector3 a, Vector3 b)
    {
        var qa = Q(a); var qb = Q(b);
        var key = qa.CompareTo(qb) < 0 ? (qa, qb) : (qb, qa);
        posEdge[key] = posEdge.GetValueOrDefault(key) + 1;
    }
    for (int i = 0; i < merged.Indices.Length; i += 3)
    {
        var a = p[merged.Indices[i]]; var b = p[merged.Indices[i + 1]]; var c = p[merged.Indices[i + 2]];
        CountPos(a, b); CountPos(b, c); CountPos(c, a);
    }
    int holes = posEdge.Count(e => (e.Value & 1) == 1);

    // Where the holes sit along the weapon's length (its longest axis), in 8 bins from one end to the
    // other, so a blade-edge problem reads differently from a handle-wrap one.
    var (bmin, bmax) = merged.Bounds();
    var size = bmax - bmin;
    int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
    float lo = axis == 0 ? bmin.X : axis == 1 ? bmin.Y : bmin.Z;
    float span = axis == 0 ? size.X : axis == 1 ? size.Y : size.Z;
    var bins = new int[8];
    foreach (var e in posEdge.Where(e => (e.Value & 1) == 1))
    {
        var (qa, qb) = e.Key;
        float m = axis == 0 ? (qa.Item1 + qb.Item1) / 2f / 50f
                : axis == 1 ? (qa.Item2 + qb.Item2) / 2f / 50f
                : (qa.Item3 + qb.Item3) / 2f / 50f;
        int b = span > 1e-3f ? Math.Clamp((int)((m - lo) / span * 8), 0, 7) : 0;
        bins[b]++;
    }

    return (n, merged.TriangleCount, isolated1, isolated2, worst, holes, bins, axis);
}

// Loads a weapon exactly as the converter does and reports triangle quality: degenerate (zero-area)
// triangles and slivers (near-zero area but a long edge, which render as stray lines). These are the
// decimation artefacts that show up as spikes coming off a blade.
int MeshCheck(string skyrimPath, string oblivionPath, string mappingsPath, string weapon)
{
    using var obr = new ObrData(oblivionPath, mappingsPath);

    var meshPath = obr.WeaponMeshes()
        .FirstOrDefault(p => Path.GetFileName(p).Equals(weapon, StringComparison.OrdinalIgnoreCase));
    if (meshPath is null) { Console.Error.WriteLine($"no mesh named {weapon}"); return 1; }

    var staticMesh = obr.LoadStaticMesh(meshPath);
    if (staticMesh is null) { Console.Error.WriteLine($"could not load {meshPath}"); return 1; }

    var s = AnalyzeMesh(MeshPrimitive.Merge(ObrMesh.Load(staticMesh, weapon)));

    Console.WriteLine($"{weapon}: {s.Verts} verts, {s.Tris} tris  (Quality={ObrMesh.Quality})");
    Console.WriteLine($"  isolated verts (nn>1.0): {s.Isolated1}   (nn>2.0): {s.Isolated2}   most nn={s.Worst:F2}");
    Console.WriteLine($"  real holes: {s.Holes}   along axis {s.Axis} (8 bins): [{string.Join(",", s.Bins)}]");
    return 0;
}

// Runs the meshcheck analysis over every mapped weapon and prints one ranked table, worst first, so the
// whole set can be triaged at once: which meshes have real holes (open-edge cracks, the see-through gem
// problem), which have decimation spikes, and which sit near Skyrim's 65k ceiling. This is the objective
// drop list - a weapon flagged HOLES here is broken at the source, not something a template tweak fixes.
int Audit(string skyrimPath, string oblivionPath, string mappingsPath, string mappingFile)
{
    using var obr = new ObrData(oblivionPath, mappingsPath);
    var meshes = obr.WeaponMeshes().ToList();
    var sources = MappingFile.Load(mappingFile).Weapons.Select(w => w.Source).Distinct().ToList();

    var rows = new List<(string Name, int Verts, int Tris, int Iso, float Worst, int Holes, int[] Bins, string Flags)>();
    var notFound = new List<string>();

    foreach (var source in sources)
    {
        var path = meshes.FirstOrDefault(p => Path.GetFileName(p).Equals(source, StringComparison.OrdinalIgnoreCase));
        var mesh = path is null ? null : obr.LoadStaticMesh(path);
        if (mesh is null) { notFound.Add(source); continue; }

        var parts = ObrMesh.Load(mesh, source);
        if (parts.Count == 0) { notFound.Add(source); continue; }

        var s = AnalyzeMesh(MeshPrimitive.Merge(parts));

        var flags = new List<string>();
        if (s.Holes > 0) flags.Add($"HOLES:{s.Holes}");
        if (s.Isolated2 > 0) flags.Add($"SPIKES:{s.Isolated2}");
        if (s.Tris > 60000 || s.Verts > 60000) flags.Add("NEAR-CAP");

        rows.Add((source, s.Verts, s.Tris, s.Isolated2, s.Worst, s.Holes, s.Bins, string.Join(" ", flags)));
    }

    // Worst first: real holes dominate (they are the see-through breaks), then spikes, then size.
    rows.Sort((a, b) =>
    {
        int c = b.Holes.CompareTo(a.Holes);
        if (c != 0) return c;
        c = b.Iso.CompareTo(a.Iso);
        return c != 0 ? c : b.Tris.CompareTo(a.Tris);
    });

    Console.WriteLine($"{"weapon",-30} {"verts",7} {"tris",7} {"spikes",6} {"holes",5}  flags");
    Console.WriteLine(new string('-', 78));
    foreach (var r in rows)
        Console.WriteLine($"{r.Name,-30} {r.Verts,7} {r.Tris,7} {r.Iso,6} {r.Holes,5}  {r.Flags}");

    int clean = rows.Count(r => r.Flags.Length == 0);
    int holed = rows.Count(r => r.Holes > 0);
    int spiked = rows.Count(r => r.Iso > 0);
    Console.WriteLine();
    Console.WriteLine($"{rows.Count} analysed: {clean} clean, {holed} with holes, {spiked} with spikes");
    if (notFound.Count > 0)
        Console.WriteLine($"{notFound.Count} could not load: {string.Join(", ", notFound)}");
    return 0;
}

// Flat-shaded orthographic render of a converted weapon straight from the imported geometry, so mesh
// defects (notches, holes, decimation damage) can be seen without loading it into a game. Looks down
// the given axis; open edges are drawn red so cracks stand out.
int Render(string skyrimPath, string oblivionPath, string mappingsPath, string weapon, string viewAxisArg)
{
    using var obr = new ObrData(oblivionPath, mappingsPath);
    var meshPath = obr.WeaponMeshes()
        .FirstOrDefault(p => Path.GetFileName(p).Equals(weapon, StringComparison.OrdinalIgnoreCase));
    if (meshPath is null) { Console.Error.WriteLine($"no mesh named {weapon}"); return 1; }
    var staticMesh = obr.LoadStaticMesh(meshPath);
    if (staticMesh is null) { Console.Error.WriteLine("load failed"); return 1; }

    var m = MeshPrimitive.Merge(ObrMesh.Load(staticMesh, weapon));
    int view = int.Parse(viewAxisArg);
    int ax = view == 0 ? 1 : 0, ay = view == 2 ? 1 : 2;

    var (bmin, bmax) = m.Bounds();
    float Comp(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
    float spanU = Comp(bmax, ax) - Comp(bmin, ax), spanV = Comp(bmax, ay) - Comp(bmin, ay);
    const int H = 1400;
    int W = Math.Clamp((int)(H * (spanU / MathF.Max(spanV, 1e-3f))) + 40, 200, 2200);

    var col = MeshRaster.Render(m, view, W, H, 20);
    var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(W, H);
    for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            img[x, y] = col[y * W + x];

    var outPng = Path.Combine(Path.GetTempPath(), $"render_{weapon}_v{view}.png");
    img.SaveAsPng(outPng);
    Console.WriteLine(outPng);
    return 0;
}

// Renders every mapped weapon into montage sheets, so the whole set can be eyeballed at once for
// mesh breaks (holes and stray lines show red). Weapons that fail to load are listed. Prints the
// tile order so a broken tile maps back to its weapon.
int Contact(string skyrimPath, string oblivionPath, string mappingsPath, string mappingFile, string outDir)
{
    using var obr = new ObrData(oblivionPath, mappingsPath);
    var sources = MappingFile.Load(mappingFile).Weapons.Select(w => w.Source).Distinct().ToList();
    Directory.CreateDirectory(outDir);

    const int tw = 200, th = 300, cols = 8, perSheet = 40;
    var meshes = obr.WeaponMeshes().ToList();
    int sheet = 0, placed = 0;
    SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>? img = null;

    void Flush()
    {
        if (img is null) return;
        img.SaveAsPng(Path.Combine(outDir, $"contact_{sheet}.png"));
        img.Dispose();
        img = null;
    }

    for (int idx = 0; idx < sources.Count; idx++)
    {
        int slot = placed % perSheet;
        if (slot == 0)
        {
            Flush();
            int rows = (int)Math.Ceiling(Math.Min(perSheet, sources.Count - placed) / (double)cols);
            img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(cols * tw, rows * th);
            sheet++;
        }

        var source = sources[idx];
        var path = meshes.FirstOrDefault(p => Path.GetFileName(p).Equals(source, StringComparison.OrdinalIgnoreCase));
        var mesh = path is null ? null : obr.LoadStaticMesh(path);
        Console.WriteLine($"  sheet {sheet} slot {slot,2}: {source}" + (mesh is null ? "  (NOT FOUND)" : ""));
        if (mesh is null) { placed++; continue; }

        var merged = MeshPrimitive.Merge(ObrMesh.Load(mesh, source));
        var col = MeshRaster.Render(merged, 2, tw, th, 6);

        int ox = (slot % cols) * tw, oy = (slot / cols) * th;
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                var c = col[y * tw + x];
                if (c.A > 0) img![ox + x, oy + y] = c;
            }

        placed++;
    }

    Flush();
    Console.WriteLine($"wrote {sheet} sheet(s) to {outDir}");
    return 0;
}

int Compare(string skyrimPath, string oblivionPath, string mappingsPath, string weapon, string template)
{
    using var obr = new ObrData(oblivionPath, mappingsPath);
    using var skyrim = new SkyrimData(skyrimPath);

    var meshPath = obr.WeaponMeshes()
        .FirstOrDefault(p => Path.GetFileName(p).Equals(weapon, StringComparison.OrdinalIgnoreCase));

    if (meshPath is null)
    {
        Console.Error.WriteLine($"no mesh named {weapon}");
        return 1;
    }

    var staticMesh = obr.LoadStaticMesh(meshPath);
    if (staticMesh is null)
    {
        Console.Error.WriteLine($"could not load {meshPath}");
        return 1;
    }

    var parts = ObrMesh.Load(staticMesh, weapon);
    if (parts.Count == 0)
    {
        Console.Error.WriteLine($"{weapon} holds no geometry");
        return 1;
    }

    var merged = MeshPrimitive.Merge(parts);
    var (min, max) = Converter.SkyrimBounds(merged);

    // Raw, before the axis convention is applied: what the asset actually holds.
    var (rawMin, rawMax) = merged.Bounds();
    Console.WriteLine($"{weapon} as authored");
    Console.WriteLine($"  x {rawMin.X,8:F1} .. {rawMax.X,8:F1}");
    Console.WriteLine($"  y {rawMin.Y,8:F1} .. {rawMax.Y,8:F1}");
    Console.WriteLine($"  z {rawMin.Z,8:F1} .. {rawMax.Z,8:F1}");
    Console.WriteLine();

    Console.WriteLine($"{weapon}");
    Console.WriteLine($"  x {min.X,8:F1} .. {max.X,8:F1}   centre {(min.X + max.X) / 2,7:F1}");
    Console.WriteLine($"  y {min.Y,8:F1} .. {max.Y,8:F1}   centre {(min.Y + max.Y) / 2,7:F1}");
    Console.WriteLine($"  z {min.Z,8:F1} .. {max.Z,8:F1}   centre {(min.Z + max.Z) / 2,7:F1}");

    var scratch = Path.Combine(Path.GetTempPath(), "compare.nif");
    File.WriteAllBytes(scratch, skyrim.Read(template));

    using var nif = new Nif(scratch);
    Console.WriteLine();
    Console.WriteLine($"{Path.GetFileName(template)}");

    for (int i = 0; i < nif.ShapeCount; i++)
    {
        var (lo, hi, _) = nif.Bounds(i);
        Console.WriteLine($"  {nif.ShapeName(i)}");
        Console.WriteLine($"    x {lo[0],8:F1} .. {hi[0],8:F1}   centre {(lo[0] + hi[0]) / 2,7:F1}");
        Console.WriteLine($"    y {lo[1],8:F1} .. {hi[1],8:F1}   centre {(lo[1] + hi[1]) / 2,7:F1}");
        Console.WriteLine($"    z {lo[2],8:F1} .. {hi[2],8:F1}   centre {(lo[2] + hi[2]) / 2,7:F1}");
    }

    File.Delete(scratch);
    return 0;
}

// Where each shape sits in the node tree, what transform it inherits, and which textures it uses.
// The target is either a path inside the game's Data, or a file on disk, so a converted mesh can be
// held against the vanilla one it came from.
int Nodes(string skyrimPath, string target)
{
    string scratch;
    bool temporary;

    if (File.Exists(target))
    {
        scratch = target;
        temporary = false;
    }
    else
    {
        using var skyrim = new SkyrimData(skyrimPath);
        scratch = Path.Combine(Path.GetTempPath(), "nodes.nif");
        File.WriteAllBytes(scratch, skyrim.Read(target));
        temporary = true;
    }

    try
    {
        using var nif = new Nif(scratch);

        Console.WriteLine($"{Path.GetFileName(target)}  " +
                          $"{nif.ShapeCount} shapes, {nif.BlockCount} blocks, stream {nif.StreamVersion}");

        for (int i = 0; i < nif.ShapeCount; i++)
        {
            int parents = nif.ParentCount(i);
            var chain = new List<string>();
            for (int level = 0; level < parents; level++)
                chain.Add(nif.ParentName(i, level));

            Console.WriteLine();
            Console.WriteLine($"  [{i}] {nif.ShapeName(i)}  [{ShapeRoles.Classify(nif, i)}]  " +
                              $"({nif.ShapeBlockType(i)}, {nif.VertexCount(i)} verts, {nif.TriangleCount(i)} tris" +
                              $"{(nif.IsSkinned(i) ? ", skinned" : "")}{(nif.IsDoubleSided(i) ? ", 2sided" : "")})");
            Console.WriteLine($"    under {(chain.Count == 0 ? "(nothing)" : string.Join(" <- ", chain))}");

            foreach (var (slot, label) in new[] { (0, "diffuse"), (1, "normal"), (2, "glow[2]"),
                (3, "height[3]"), (4, "cubemap[4]"), (5, "env mask"), (6, "tint[6]"), (7, "spec[7]") })
            {
                var texture = nif.Texture(i, slot);
                if (texture.Length > 0)
                    Console.WriteLine($"    {label,-9} {texture}");
            }

            // Local bounds are how a shape is modelled, world bounds where it ends up. Two shapes
            // placed by different nodes can only be compared on one or the other, never across.
            if (nif.VertexCount(i) > 0)
            {
                Box("local", nif.LocalBounds(i));
                Box("world", nif.Bounds(i));
            }

            Print("self", nif.NodeTransform(i, -1));
            for (int level = 0; level < parents; level++)
                Print(chain[level], nif.NodeTransform(i, level));
            Print("world", nif.WorldTransform(i));
        }
    }
    finally
    {
        if (temporary)
            File.Delete(scratch);
    }

    return 0;

    static void Box(string label, (float[] Min, float[] Max, float[] Size) bounds)
    {
        Console.WriteLine($"    {label,-8} x {bounds.Min[0],8:F1} .. {bounds.Max[0],8:F1}   " +
                          $"y {bounds.Min[1],8:F1} .. {bounds.Max[1],8:F1}   " +
                          $"z {bounds.Min[2],8:F1} .. {bounds.Max[2],8:F1}");
    }

    static void Print(string label, Nif.Transform transform)
    {
        var t = transform.Translation;
        Console.Write($"      {label,-24} translate {t[0],8:F2} {t[1],8:F2} {t[2],8:F2}" +
                      $"   scale {transform.Scale,6:F3}");

        if (transform.IsIdentityRotation)
        {
            Console.WriteLine("   rotation identity");
            return;
        }

        var r = transform.Rotation;
        Console.WriteLine();
        for (int row = 0; row < 3; row++)
            Console.WriteLine($"      {"",-24} rotation  {r[row * 3],7:F3} {r[row * 3 + 1],7:F3} {r[row * 3 + 2],7:F3}");
    }
}

// Classifies every shape of every mapped template, to check the naming convention actually holds
// across the whole set before the converter relies on it. Reports whatever it could not place.
int Survey(string skyrimPath, string mappingFile)
{
    var mapping = MappingFile.Load(mappingFile);
    using var skyrim = new SkyrimData(skyrimPath);

    var roleCounts = new SortedDictionary<ShapeRole, int>();
    var unknownNames = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var missingWeapon = new List<string>();
    var shapeCounts = new SortedDictionary<int, int>();
    int templatesRead = 0;
    int withScabbard = 0;

    foreach (var weapon in mapping.Weapons)
    {
        foreach (var template in new[] { weapon.Template, Pipeline.FirstPerson(weapon.Template) })
        {
            if (skyrim.Describe(template) == "not found")
                continue;

            var scratch = Path.Combine(Path.GetTempPath(), "survey.nif");
            File.WriteAllBytes(scratch, skyrim.Read(template));

            try
            {
                using var nif = new Nif(scratch);
                templatesRead++;

                var summary = new List<string>();
                int weaponShapes = 0;
                int scabbardShapes = 0;

                shapeCounts.TryGetValue(nif.ShapeCount, out int seenCount);
                shapeCounts[nif.ShapeCount] = seenCount + 1;

                for (int i = 0; i < nif.ShapeCount; i++)
                {
                    var name = nif.ShapeName(i);
                    var role = ShapeRoles.Classify(nif, i);

                    roleCounts.TryGetValue(role, out int count);
                    roleCounts[role] = count + 1;

                    if (role == ShapeRole.Weapon)
                        weaponShapes++;

                    if (role == ShapeRole.Scabbard)
                        scabbardShapes++;

                    if (role == ShapeRole.Unknown)
                    {
                        if (!unknownNames.TryGetValue(name, out var owners))
                            unknownNames[name] = owners = new List<string>();
                        owners.Add(Path.GetFileName(template));
                    }

                    summary.Add($"{name}[{role}]");
                }

                if (weaponShapes == 0)
                    missingWeapon.Add(template);

                if (scabbardShapes > 0)
                    withScabbard++;

                Console.WriteLine($"  {Path.GetFileName(template),-32} {string.Join("  ", summary)}");
            }
            finally
            {
                File.Delete(scratch);
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{templatesRead} templates read from {mapping.Weapons.Count} mapped weapons");
    Console.WriteLine();

    foreach (var (role, count) in roleCounts)
        Console.WriteLine($"  {role,-12} {count,4} shapes");

    Console.WriteLine();
    Console.WriteLine("  shapes per template:");
    foreach (var (count, templates) in shapeCounts)
        Console.WriteLine($"    {count,2} shapes  x{templates}");

    Console.WriteLine();
    Console.WriteLine($"  {withScabbard} of {templatesRead} templates carry a scabbard");

    if (missingWeapon.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  {missingWeapon.Count} templates with no weapon shape at all:");
        foreach (var template in missingWeapon)
            Console.WriteLine($"    {template}");
    }

    if (unknownNames.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  {unknownNames.Count} unrecognised shape names:");
        foreach (var (name, owners) in unknownNames)
            Console.WriteLine($"    {name,-28} in {owners.Count}: {string.Join(", ", owners.Take(3))}");
    }

    return 0;
}

// Skyrim's mesh catalogue, to see what is there to aim at.
int List(string skyrimPath, string? filter)
{
    using var skyrim = new SkyrimData(skyrimPath);

    var meshes = skyrim.FilesUnder("meshes", ".nif")
        .Where(p => filter is null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToList();

    foreach (var mesh in meshes)
        Console.WriteLine($"  {mesh}");

    Console.WriteLine();
    Console.WriteLine($"{meshes.Count} meshes");
    return 0;
}

// Oblivion's mesh catalogue, the other half of any mapping decision.
int Assets(string oblivionPath, string mappingsPath, string? filter)
{
    using var obr = new ObrData(oblivionPath, mappingsPath);

    var meshes = obr.Meshes(filter)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToList();

    foreach (var mesh in meshes)
        Console.WriteLine($"  {mesh}");

    Console.WriteLine();
    Console.WriteLine($"{meshes.Count} meshes");
    return 0;
}

static void Usage()
{
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  obr2sse probe   <oblivion> [mappings.usmap] [asset]");
    Console.Error.WriteLine("  obr2sse propose <skyrim> <oblivion> <mappings.usmap> <out.json>");
    Console.Error.WriteLine("  obr2sse catalog <oblivion> <mappings.usmap> <out.json>");
    Console.Error.WriteLine("  obr2sse convert <skyrim> <oblivion> <mappings.usmap> <output dir>            standalone (default)");
    Console.Error.WriteLine("  obr2sse convert <skyrim> <oblivion> <mappings.usmap> <weapons.json> <output dir> --replacer");
    Console.Error.WriteLine("  obr2sse esp     <oblivion> <mappings.usmap> <output dir>                     rebuild the standalone esp only");
    Console.Error.WriteLine("  obr2sse one     <skyrim> <oblivion> <mappings.usmap> <weapon> <template> <output dir>");
    Console.Error.WriteLine("  obr2sse compare <skyrim> <oblivion> <mappings.usmap> <weapon> <template>");
    Console.Error.WriteLine("  obr2sse nodes   <skyrim> <template or file on disk>");
    Console.Error.WriteLine("  obr2sse survey  <skyrim> <weapons.json>");
    Console.Error.WriteLine("  obr2sse list    <skyrim> [substring]");
    Console.Error.WriteLine("  obr2sse assets  <oblivion> <mappings.usmap> [substring]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  --flip-v    invert the uv v axis");
    Console.Error.WriteLine("  --no-fit    keep each piece's authored length, not the template's");
    Console.Error.WriteLine("  --balanced  simplify meshes to about 10k triangles");
    Console.Error.WriteLine("  --vanilla   simplify meshes to about 4k triangles");
    Console.Error.WriteLine("  --bc7       higher quality textures, much slower");
    Console.Error.WriteLine("  --report    print each weapon's length before and after");
}
