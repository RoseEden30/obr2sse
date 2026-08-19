using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Options;

namespace Obr2Sse;

/// Pulls geometry out of an Unreal static mesh, one primitive per material section.
///
/// Sections index into one shared vertex buffer, so each is rebased onto its own vertex array:
/// Skyrim shapes own their vertices, and its indices are 16 bit.
public static class ObrMesh
{
    public static List<MeshPrimitive> Load(UStaticMesh mesh, string name)
    {
        // Oblivion's weapons are Nanite meshes. The render-data LOD 0 is Nanite's own fallback proxy,
        // decimated so hard that fine features - a crossguard, a mace's collar rings - come out torn
        // and full of holes. The real geometry lives in the Nanite pages, decoded here instead.
        if (mesh.TryConvert(out var dto, EMeshQuality.All, ENaniteMeshFormat.NaniteOnly) &&
            dto.LODs.Count > 0 && dto.LODs[0].IsNanite && dto.LODs[0].Vertices.Length > 0)
        {
            return FromNanite(mesh, dto.LODs[0], name);
        }

        var lods = mesh.RenderData?.LODs;
        if (lods is not { Length: > 0 })
            return new List<MeshPrimitive>();

        var lod = lods[0];
        var result = new List<MeshPrimitive>();

        var positions = lod.PositionVertexBuffer?.Verts;
        var vertices = lod.VertexBuffer?.UV;
        var indices = lod.IndexBuffer?.Buffer?.Select(i => (uint)i).ToArray();

        if (positions is null || indices is null)
            return result;

        foreach (var section in lod.Sections ?? Array.Empty<FStaticMeshSection>())
        {
            var map = new Dictionary<uint, uint>();
            var localPositions = new List<Vector3>();
            var localNormals = new List<Vector3>();
            var localUvs = new List<Vector2>();
            var localIndices = new List<uint>();

            for (int i = 0; i < section.NumTriangles * 3; i++)
            {
                uint global = indices[section.FirstIndex + i];

                if (!map.TryGetValue(global, out uint local))
                {
                    local = (uint)localPositions.Count;
                    map[global] = local;

                    var p = positions[global];
                    localPositions.Add(new Vector3(p.X, p.Y, p.Z));

                    if (vertices is not null && global < vertices.Length)
                    {
                        var n = vertices[global].Normal[2];
                        localNormals.Add(new Vector3(n.X, n.Y, n.Z));

                        var uv = vertices[global].UV[0];
                        localUvs.Add(new Vector2(uv.U, uv.V));
                    }
                }

                localIndices.Add(local);
            }

            result.Add(new MeshPrimitive
            {
                Name = name,
                MaterialIndex = section.MaterialIndex,
                MaterialName = MaterialName(mesh, section.MaterialIndex),
                Positions = localPositions.ToArray(),
                Normals = localNormals.ToArray(),
                TexCoords = localUvs.ToArray(),
                Indices = localIndices.ToArray(),
            });
        }

        return result;
    }

    /// How much of the Nanite detail to keep. High is the full Oblivion geometry, reduced only when
    /// a shape would otherwise blow the 16-bit vertex ceiling; the others trade detail for size.
    public enum MeshQuality { High, Balanced, Vanilla }

    public static MeshQuality Quality { get; set; } = MeshQuality.High;

    /// The vertex-weld resolution: 1/cell-size, so 50 welds within 0.02 units. Coarse enough to fold
    /// Nanite's per-cluster duplicates (about 0.008 apart) back together.
    public const float WeldScale = 50f;

    /// How finely the normal is folded into the weld key. A hard edge - a blade's ridgeline, a bevel -
    /// has two faces whose normals differ sharply; keeping the normal in the key stops them welding
    /// into one averaged normal, which would crease the shading. Coarse enough that a smooth surface's
    /// rounding noise still welds.
    private const float NormalWeld = 4f;

    /// A BSTriShape indexes its vertices with 16-bit triangles, so a shape holds at most 65535. This
    /// is the ceiling the total across sections is held under, a hair below it for safety.
    private const int VertexLimit = 65000;

    /// Skyrim serialises a BSTriShape's triangle count as a 16-bit field (stream 100), so a shape can
    /// hold at most 65535 triangles as well. Over this the count wraps on save and most of the mesh is
    /// left orphaned - a crashing shred. Held a hair below, like the vertex ceiling.
    private const int TriangleLimit = 65000;

    /// The triangle budget for a quality, or null to keep the full mesh (High).
    private static int? Budget => Quality switch
    {
        MeshQuality.Balanced => 10000,
        MeshQuality.Vanilla => 4000,
        _ => null,
    };

    /// Builds one primitive per material section from a decoded Nanite LOD. The whole mesh is
    /// simplified together - all sections at once - so material seams are preserved and no crack
    /// opens between them. High keeps every triangle Oblivion authored unless the shape would exceed
    /// the vertex ceiling, in which case it is thinned just enough to fit.
    private static List<MeshPrimitive> FromNanite(UStaticMesh mesh, MeshLodDto<MeshVertex> lod, string name)
    {
        var verts = lod.Vertices;

        // Nanite duplicates vertices along cluster boundaries; welding them folds the mesh back into
        // one connected surface the simplifier can reduce. The position tolerance is coarser than
        // Nanite's ~0.008 quantisation so boundary copies meet, while uv and normal stay in the key to
        // keep texture seams and hard edges (a thin blade's two opposite faces) from collapsing.
        var weld = new Dictionary<(int, int, int, int, int, int, int, int), int>();
        var remap = new int[verts.Length];
        var positionList = new List<MeshDecimator.Math.Vector3d>();
        var normalList = new List<MeshDecimator.Math.Vector3>();
        var uvList = new List<MeshDecimator.Math.Vector2>();

        for (int i = 0; i < verts.Length; i++)
        {
            var v = verts[i];
            var key = (
                (int)MathF.Round(v.Position.X * WeldScale), (int)MathF.Round(v.Position.Y * WeldScale), (int)MathF.Round(v.Position.Z * WeldScale),
                (int)MathF.Round(v.Uv.U * 512f), (int)MathF.Round(v.Uv.V * 512f),
                (int)MathF.Round(v.Normal.X * NormalWeld), (int)MathF.Round(v.Normal.Y * NormalWeld), (int)MathF.Round(v.Normal.Z * NormalWeld));

            if (!weld.TryGetValue(key, out int id))
            {
                id = positionList.Count;
                weld[key] = id;
                positionList.Add(new MeshDecimator.Math.Vector3d(v.Position.X, v.Position.Y, v.Position.Z));
                normalList.Add(new MeshDecimator.Math.Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z));
                uvList.Add(new MeshDecimator.Math.Vector2(v.Uv.U, v.Uv.V));
            }

            remap[i] = id;
        }

        var positions = positionList.ToArray();
        var normals = normalList.ToArray();
        var uvs = uvList.ToArray();

        var sections = lod.Sections.Where(s => s.NumFaces > 0).ToArray();
        var submeshes = new int[sections.Length][];
        int totalTriangles = 0;

        for (int s = 0; s < sections.Length; s++)
        {
            var section = sections[s];
            var tris = new int[section.NumFaces * 3];
            for (int k = 0; k < tris.Length; k++)
                tris[k] = remap[(int)lod.Indices[section.FirstIndex + k]];
            submeshes[s] = tris;
            totalTriangles += section.NumFaces;
        }

        var full = new MeshDecimator.Mesh(positions, submeshes) { Normals = normals };
        full.SetUVs(0, uvs);

        // One primitive per material section, each section rebased onto its own vertex array. The
        // rebasing duplicates a section's share of the seam vertices, so the true cost is the total
        // across sections - which is what the decimation below is held to.
        List<MeshPrimitive> Build(MeshDecimator.Mesh simplified)
        {
            var outPositions = simplified.Vertices;
            var outNormals = simplified.Normals;
            var outUvs = simplified.GetUVs2D(0);
            var built = new List<MeshPrimitive>();

            for (int s = 0; s < simplified.SubMeshCount; s++)
            {
                var indices = simplified.GetIndices(s);

                var map = new Dictionary<int, uint>();
                var localPositions = new List<Vector3>();
                var localNormals = new List<Vector3>();
                var localUvs = new List<Vector2>();
                var localIndices = new List<uint>();

                foreach (int global in indices)
                {
                    if (!map.TryGetValue(global, out uint local))
                    {
                        local = (uint)localPositions.Count;
                        map[global] = local;

                        var p = outPositions[global];
                        localPositions.Add(new Vector3((float)p.x, (float)p.y, (float)p.z));
                        var n = outNormals[global];
                        localNormals.Add(new Vector3((float)n.x, (float)n.y, (float)n.z));
                        var uv = outUvs[global];
                        localUvs.Add(new Vector2((float)uv.x, (float)uv.y));
                    }

                    localIndices.Add(local);
                }

                built.Add(new MeshPrimitive
                {
                    Name = name,
                    MaterialIndex = sections[s].MaterialIndex,
                    MaterialName = MaterialName(mesh, sections[s].MaterialIndex),
                    Positions = localPositions.ToArray(),
                    Normals = localNormals.ToArray(),
                    TexCoords = localUvs.ToArray(),
                    Indices = localIndices.ToArray(),
                });
            }

            return built;
        }

        // The ceiling is on the shape's own shared vertex buffer, which is the decimated mesh's count.
        // The per-section rebasing duplicates the seam vertices, but Merge folds those back together
        // before the geometry becomes a shape, so this is the number that actually has to fit.
        var result = Build(full);

        // High keeps the full mesh unless it overruns a ceiling; the budgets always simplify. Both the
        // vertex and the triangle ceilings have to be met - a shape well under the vertex limit can
        // still carry more triangles than the 16-bit count can hold. The target is lowered and re-run
        // until the mesh fits, decimating the full mesh each pass so quality does not compound.
        int? target = null;
        if (Budget is int budget && budget < totalTriangles)
            target = budget;
        else if (Budget is null)
        {
            int cap = totalTriangles;
            if (positions.Length > VertexLimit)
                cap = Math.Min(cap, (int)((long)totalTriangles * VertexLimit / positions.Length));
            if (totalTriangles > TriangleLimit)
                cap = Math.Min(cap, TriangleLimit);
            if (cap < totalTriangles)
                target = cap;
        }

        while (target is int tris && tris > 200)
        {
            var algorithm = new MeshDecimator.Algorithms.FastQuadricMeshSimplification
            {
                PreserveBorders = false,
                PreserveSeams = false,
            };
            var simplified = MeshDecimator.MeshDecimation.DecimateMesh(algorithm, full, tris);
            result = Build(simplified);

            int builtTriangles = result.Sum(p => p.TriangleCount);
            if (simplified.Vertices.Length <= VertexLimit && builtTriangles <= TriangleLimit)
                break;

            target = (int)(tris * 0.75);
        }

        // Decimation leaves needle triangles - a far vertex tied to a near-coincident pair - that hang
        // off the blade as stray lines. Cleaning collapses them away; a collapse can spawn a smaller
        // needle in turn, so it is repeated until the mesh stops changing (a few passes in practice).
        for (int pass = 0; pass < 6; pass++)
        {
            var cleaned = result.Select(Clean).ToList();
            bool changed = cleaned.Zip(result, (a, b) => a.Indices.Length != b.Indices.Length).Any(x => x);
            result = cleaned;
            if (!changed)
                break;
        }

        return result;
    }

    /// Removes decimation spikes and degenerate triangles without tearing the surface.
    ///
    /// A spike is a vertex stranded far from the rest, joined back only by a fan of thin triangles -
    /// a stray line off a blade. It's folded into its nearest neighbour so the fan flattens onto the
    /// surface; zero-area triangles collapse along their shortest edge. Both go through a union-find,
    /// and vertices left unreferenced afterwards are dropped.
    private static MeshPrimitive Clean(MeshPrimitive prim)
    {
        var p = prim.Positions;
        var idx = prim.Indices;
        int n = p.Length;

        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
                parent[rb] = ra;
        }

        // Each vertex's nearest connected neighbour.
        var nearestDist = new float[n];
        var nearestIdx = new int[n];
        Array.Fill(nearestDist, float.MaxValue);
        Array.Fill(nearestIdx, -1);

        void Edge(int a, int b)
        {
            float d = (p[a] - p[b]).Length();
            if (d < nearestDist[a]) { nearestDist[a] = d; nearestIdx[a] = b; }
            if (d < nearestDist[b]) { nearestDist[b] = d; nearestIdx[b] = a; }
        }

        for (int i = 0; i < idx.Length; i += 3)
        {
            int ia = (int)idx[i], ib = (int)idx[i + 1], ic = (int)idx[i + 2];
            Edge(ia, ib);
            Edge(ib, ic);
            Edge(ic, ia);
        }

        // The typical closest-neighbour spacing, from the median of those distances. This tracks the
        // mesh's own density, coarse or fine, so the spike test scales with it rather than a fixed size.
        var spacing = nearestDist.Where(d => d != float.MaxValue).OrderBy(d => d).ToArray();
        if (spacing.Length == 0)
            return prim;

        float typical = spacing[spacing.Length / 2];

        // A vertex whose closest neighbour is several times the typical spacing away is a spike - the
        // far tip of a needle fan, tied to the surface only by slivers. Fold it into that neighbour
        // (the neighbour is the survivor, so the fan flattens onto the surface, not out to the tip).
        float isolated = MathF.Max(1.5f, typical * 5f);
        for (int v = 0; v < n; v++)
        {
            if (nearestIdx[v] >= 0 && nearestDist[v] > isolated)
                Union(nearestIdx[v], v);
        }

        // Zero-area triangles carry no surface; collapse each along its shortest edge so it drops out.
        for (int i = 0; i < idx.Length; i += 3)
        {
            int ia = (int)idx[i], ib = (int)idx[i + 1], ic = (int)idx[i + 2];
            Vector3 a = p[ia], b = p[ib], c = p[ic];

            if (0.5f * Vector3.Cross(b - a, c - a).Length() >= 1e-4f)
                continue;

            float lab = (b - a).LengthSquared(), lbc = (c - b).LengthSquared(), lca = (a - c).LengthSquared();
            if (lab <= lbc && lab <= lca) Union(ia, ib);
            else if (lbc <= lca) Union(ib, ic);
            else Union(ic, ia);
        }

        var map = new Dictionary<int, uint>();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<uint>();

        uint Keep(int rep)
        {
            if (!map.TryGetValue(rep, out uint id))
            {
                id = (uint)positions.Count;
                map[rep] = id;
                positions.Add(prim.Positions[rep]);
                normals.Add(rep < prim.Normals.Length ? prim.Normals[rep] : default);
                uvs.Add(rep < prim.TexCoords.Length ? prim.TexCoords[rep] : default);
            }
            return id;
        }

        for (int i = 0; i < idx.Length; i += 3)
        {
            int ra = Find((int)idx[i]), rb = Find((int)idx[i + 1]), rc = Find((int)idx[i + 2]);
            if (ra == rb || rb == rc || ra == rc)
                continue;

            indices.Add(Keep(ra));
            indices.Add(Keep(rb));
            indices.Add(Keep(rc));
        }

        return new MeshPrimitive
        {
            Name = prim.Name,
            MaterialIndex = prim.MaterialIndex,
            MaterialName = prim.MaterialName,
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            TexCoords = uvs.ToArray(),
            Indices = indices.ToArray(),
        };
    }

    private static string MaterialName(UStaticMesh mesh, int index)
    {
        return Material(mesh, index)?.Name ?? "none";
    }

    /// The material a section renders with, which is what holds its texture references.
    public static UMaterialInterface? Material(UStaticMesh mesh, int index)
    {
        var materials = mesh.StaticMaterials;
        if (materials is null || index < 0 || index >= materials.Length)
            return null;

        return materials[index].MaterialInterface?.Load<UMaterialInterface>();
    }
}
