using System.Numerics;

namespace Obr2Sse;

/// Maps Oblivion Remastered geometry into Skyrim space.
public static class Converter
{
    /// Unreal is Z-up left-handed, Gamebryo is Z-up right-handed. Measured against Chillrend: the
    /// blade runs along -Y in the asset and +Y in the NIF. Both use the same unit, so no scaling.
    public static Vector3 ToSkyrim(Vector3 v) => new(-v.X, -v.Y, v.Z);

    /// A centre has to sit this far off the origin, relative to the extent, before its side is
    /// taken as meaningful. A mesh centred on its own origin has no side to speak of, and reading
    /// one off floating point noise would flip a weapon that was already right.
    private const float SideThreshold = 0.15f;

    /// Rotation that lays the source along the same axis as the template, or identity when they
    /// already agree.
    ///
    /// Weapons are modelled along their length, but nothing says which axis that is, and the two
    /// games do not always agree. Matching the longest extent of each and then the side it falls on
    /// recovers the orientation without a per-weapon table.
    ///
    /// Bounding boxes cannot tell a tip from a hilt, so a source modelled tip-first against a
    /// template modelled tip-last comes out reversed. That shows up immediately in game.
    public static Matrix4x4 AlignAxes((Vector3 Min, Vector3 Max) source, (Vector3 Min, Vector3 Max) target)
    {
        var primary = PrimaryRotation(source, target);

        // The length axis only pins down two of the three degrees of freedom; the roll about it is
        // still free. All the bounding box says about the roll is which side of the origin the mesh
        // leans to across its length, so matching that side settles it without disturbing what the
        // first rotation just fixed.
        var rotated = Transform(source, primary);
        var length = Abs(DominantAxis(target.Min, target.Max));

        foreach (var axis in SecondaryAxes(target, length))
        {
            int here = Side(rotated, axis);
            int there = Side(target, axis);

            if (here == 0 || there == 0)
                continue;

            return here == there
                ? primary
                : primary * Matrix4x4.CreateFromAxisAngle(length, MathF.PI);
        }

        return primary;
    }

    /// Rotation that lays the source's longest axis along the template's.
    private static Matrix4x4 PrimaryRotation((Vector3 Min, Vector3 Max) source, (Vector3 Min, Vector3 Max) target)
    {
        var from = DominantAxis(source.Min, source.Max);
        var to = DominantAxis(target.Min, target.Max);

        if (Vector3.Dot(Abs(from), Abs(to)) > 0.5f)
        {
            // Same axis: either nothing to do, or the two lie on opposite sides of the origin.
            if (Vector3.Dot(from, to) > 0f)
                return Matrix4x4.Identity;

            return Matrix4x4.CreateFromAxisAngle(Perpendicular(from), MathF.PI);
        }

        // Perpendicular axes: one quarter turn about their cross product takes one onto the other.
        return Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(from, to)), MathF.PI / 2f);
    }

    /// The two axes across the length, the one the template leans on hardest first.
    private static IEnumerable<Vector3> SecondaryAxes((Vector3 Min, Vector3 Max) target, Vector3 length)
    {
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }
            .Where(axis => Vector3.Dot(axis, length) < 0.5f)
            .OrderByDescending(axis => Lean(target, axis));

        return axes;
    }

    /// How far off centre a box sits along one axis, as a fraction of its own extent there.
    private static float Lean((Vector3 Min, Vector3 Max) bounds, Vector3 axis)
    {
        float extent = Vector3.Dot(bounds.Max - bounds.Min, axis);
        if (extent <= 0f)
            return 0f;

        return MathF.Abs(Vector3.Dot(bounds.Max + bounds.Min, axis) / 2f) / extent;
    }

    /// Which side of the origin a box sits on along one axis, or nothing when it straddles it.
    private static int Side((Vector3 Min, Vector3 Max) bounds, Vector3 axis)
    {
        if (Lean(bounds, axis) < SideThreshold)
            return 0;

        return Vector3.Dot(bounds.Max + bounds.Min, axis) < 0f ? -1 : 1;
    }

    /// The box a box becomes once turned, taken over its eight corners.
    private static (Vector3 Min, Vector3 Max) Transform((Vector3 Min, Vector3 Max) bounds, Matrix4x4 rotation)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                                    (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                                    (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);

            var turned = Vector3.Transform(point, rotation);
            min = Vector3.Min(min, turned);
            max = Vector3.Max(max, turned);
        }

        return (min, max);
    }

    /// The axis a mesh is longest along, signed by the side of the origin it sits on. Zero on that
    /// component when the mesh straddles the origin, which reads as "no preference".
    private static Vector3 DominantAxis(Vector3 min, Vector3 max)
    {
        var size = max - min;
        var centre = (min + max) / 2f;

        var (axis, extent, offset) = size.X >= size.Y && size.X >= size.Z
            ? (Vector3.UnitX, size.X, centre.X)
            : size.Y >= size.Z
                ? (Vector3.UnitY, size.Y, centre.Y)
                : (Vector3.UnitZ, size.Z, centre.Z);

        if (extent <= 0f || MathF.Abs(offset) < extent * SideThreshold)
            return axis;

        return offset < 0f ? -axis : axis;
    }

    private static Vector3 Abs(Vector3 v) => new(MathF.Abs(v.X), MathF.Abs(v.Y), MathF.Abs(v.Z));

    /// Any coordinate axis at right angles to the given one, to turn about.
    private static Vector3 Perpendicular(Vector3 axis)
    {
        return MathF.Abs(axis.X) > 0.5f ? Vector3.UnitZ : Vector3.UnitX;
    }

    /// Injects a primitive into a shape of an already-loaded NIF.
    ///
    /// Negating one axis flips handedness, so triangle winding is reversed to match or every face
    /// would point inward and cull away. The alignment rotation is a proper rotation and leaves
    /// winding alone.
    public static void Inject(Nif nif, int shapeIndex, MeshPrimitive prim, bool flipV,
                              Matrix4x4 rotation, Vector3 offset)
    {
        int count = prim.Positions.Length;

        var points = new List<Vector3>(count);
        for (int i = 0; i < count; i++)
            points.Add(Vector3.Transform(ToSkyrim(prim.Positions[i]), rotation) + offset);

        List<Vector3>? normals = null;
        if (prim.Normals.Length == count)
        {
            // Normals do not ride the same matrix as positions once a scale is uneven: squashing a
            // shape tilts its faces the other way. The inverse transpose is the one that follows.
            var normalMatrix = Matrix4x4.Invert(rotation, out var inverse)
                ? Matrix4x4.Transpose(inverse)
                : rotation;

            normals = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                normals.Add(Vector3.Normalize(
                    Vector3.TransformNormal(ToSkyrim(prim.Normals[i]), normalMatrix)));
            }
        }

        var uvs = prim.TexCoords.Length == count ? new List<Vector2>(prim.TexCoords) : null;

        var triangles = new List<uint>(prim.Indices.Length);
        for (int i = 0; i + 2 < prim.Indices.Length; i += 3)
        {
            triangles.Add(prim.Indices[i]);
            triangles.Add(prim.Indices[i + 2]);
            triangles.Add(prim.Indices[i + 1]);
        }

        nif.SetGeometry(shapeIndex, shapeIndex,
                        Flatten(points),
                        normals is null ? null : Flatten(normals),
                        uvs is null ? null : Flatten(uvs, flipV),
                        points.Count, triangles.ToArray(), triangles.Count / 3);
    }

    private static float[] Flatten(List<Vector3> values)
    {
        var flat = new float[values.Count * 3];

        for (int i = 0; i < values.Count; i++)
        {
            flat[i * 3] = values[i].X;
            flat[i * 3 + 1] = values[i].Y;
            flat[i * 3 + 2] = values[i].Z;
        }

        return flat;
    }

    private static float[] Flatten(List<Vector2> values, bool flipV)
    {
        var flat = new float[values.Count * 2];

        for (int i = 0; i < values.Count; i++)
        {
            flat[i * 2] = values[i].X;
            flat[i * 2 + 1] = flipV ? 1f - values[i].Y : values[i].Y;
        }

        return flat;
    }

    /// Bounds of a primitive once mapped into Skyrim space, before any offset.
    public static (Vector3 Min, Vector3 Max) SkyrimBounds(MeshPrimitive prim) =>
        SkyrimBounds(prim, Matrix4x4.Identity);

    public static (Vector3 Min, Vector3 Max) SkyrimBounds(MeshPrimitive prim, Matrix4x4 rotation)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var p in prim.Positions)
        {
            var v = Vector3.Transform(ToSkyrim(p), rotation);
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        return (min, max);
    }

    /// Bounds of a whole set of primitives, in Skyrim space.
    public static (Vector3 Min, Vector3 Max) SkyrimBounds(IEnumerable<MeshPrimitive> parts) =>
        SkyrimBounds(parts, Matrix4x4.Identity);

    public static (Vector3 Min, Vector3 Max) SkyrimBounds(IEnumerable<MeshPrimitive> parts, Matrix4x4 rotation)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var prim in parts)
        {
            var (lo, hi) = SkyrimBounds(prim, rotation);
            min = Vector3.Min(min, lo);
            max = Vector3.Max(max, hi);
        }

        return (min, max);
    }
}
