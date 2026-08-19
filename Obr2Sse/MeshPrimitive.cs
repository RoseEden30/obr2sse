using System.Numerics;

namespace Obr2Sse;

/// One material section of geometry, whatever it was read from.
public sealed class MeshPrimitive
{
    public required string Name { get; init; }
    public required int MaterialIndex { get; init; }
    public required string MaterialName { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] TexCoords { get; init; }
    public required uint[] Indices { get; init; }

    public int TriangleCount => Indices.Length / 3;

    /// Merges several primitives into one, for templates that keep the whole weapon in a single
    /// shape. Oblivion splits by material, Skyrim does not always, and the split carries no meaning
    /// on the Skyrim side beyond which texture set a shape uses.
    public static MeshPrimitive Merge(IReadOnlyList<MeshPrimitive> parts)
    {
        if (parts.Count == 1)
            return parts[0];

        // Welds while it merges. Where a weapon's materials share vertices - an interleaved mesh whose
        // sections were rebased onto their own arrays - those copies match in position and uv and fold
        // back to one, so the combined shape is not carrying every seam vertex twice. The tolerance
        // matches the load-time weld so a shape's count stays what the decimation was held to.
        var weld = new Dictionary<(int, int, int, int, int), uint>();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<uint>();

        foreach (var part in parts)
        {
            var map = new uint[part.Positions.Length];

            for (int i = 0; i < part.Positions.Length; i++)
            {
                var p = part.Positions[i];
                var n = i < part.Normals.Length ? part.Normals[i] : default;
                var uv = i < part.TexCoords.Length ? part.TexCoords[i] : default;

                float s = ObrMesh.WeldScale;
                var key = (
                    (int)MathF.Round(p.X * s), (int)MathF.Round(p.Y * s), (int)MathF.Round(p.Z * s),
                    (int)MathF.Round(uv.X * 512f), (int)MathF.Round(uv.Y * 512f));

                if (!weld.TryGetValue(key, out uint id))
                {
                    id = (uint)positions.Count;
                    weld[key] = id;
                    positions.Add(p);
                    normals.Add(n);
                    uvs.Add(uv);
                }

                map[i] = id;
            }

            foreach (var index in part.Indices)
                indices.Add(map[index]);
        }

        return new MeshPrimitive
        {
            Name = parts[0].Name,
            MaterialIndex = parts[0].MaterialIndex,
            MaterialName = parts[0].MaterialName,
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            TexCoords = uvs.ToArray(),
            Indices = indices.ToArray(),
        };
    }

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var p in Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return (min, max);
    }
}
