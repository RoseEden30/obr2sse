using System.Runtime.InteropServices;
using System.Text;

namespace Obr2Sse;

/// Managed side of the nifly wrapper. Strings come back through caller-owned buffers.
public sealed class Nif : IDisposable
{
    private const string Library = "nifwrap";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr nif_open(string path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int nif_save(IntPtr handle, string path, int optimize, int sortBlocks);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nif_close(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint nif_version_stream(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint nif_block_count(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_count(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_name(IntPtr handle, int index, byte[]? buffer, int bufferSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_block_type(IntPtr handle, int index, byte[]? buffer, int bufferSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_is_skinned(IntPtr handle, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_parent_count(IntPtr handle, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_parent_name(IntPtr handle, int index, int level, byte[]? buffer, int bufferSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_transform(IntPtr handle, int index, int level,
        float[] translation, float[] rotation, out float scale);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_world_transform(IntPtr handle, int index,
        float[] translation, float[] rotation, out float scale);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_vertex_count(IntPtr handle, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_triangle_count(IntPtr handle, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_texture(IntPtr handle, int index, int slot, byte[]? buffer, int bufferSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_bounds(IntPtr handle, int index, float[] min, float[] max);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_bounds_local(IntPtr handle, int index, float[] min, float[] max);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_bounds_slice(IntPtr handle, int index, int axis,
        float low, float high, float[] min, float[] max);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int nif_shape_set_texture(IntPtr handle, int index, int slot, string path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_detach_textures(IntPtr handle, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_delete_shape(IntPtr handle, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_set_double_sided(IntPtr handle, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_double_sided(IntPtr handle, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_shader_type(IntPtr handle, int index, byte[]? buffer, int bufferSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_project(IntPtr handle, int index,
        int[] targets, int targetCount, float lift, float limit);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_subdivide(IntPtr handle, int index, float maxEdgeLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_remap(IntPtr handle, int index,
        float scaleX, float scaleY, float scaleZ,
        float fromX, float fromY, float fromZ,
        float toX, float toY, float toZ);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_reframe(IntPtr handle, int index, int frameIndex);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nif_shape_set_geometry(IntPtr handle, int index, int frameIndex,
        float[] positions, float[]? normals, float[]? uvs, int vertexCount,
        uint[] indices, int triangleCount);

    private IntPtr _handle;

    /// One node's placement relative to its own parent. Rotation is row major.
    public readonly record struct Transform(float[] Translation, float[] Rotation, float Scale)
    {
        public bool IsIdentity =>
            Translation.All(v => MathF.Abs(v) < 1e-4f) &&
            MathF.Abs(Scale - 1f) < 1e-4f &&
            IsIdentityRotation;

        public bool IsIdentityRotation
        {
            get
            {
                for (int row = 0; row < 3; row++)
                {
                    for (int column = 0; column < 3; column++)
                    {
                        float expected = row == column ? 1f : 0f;
                        if (MathF.Abs(Rotation[row * 3 + column] - expected) > 1e-4f)
                            return false;
                    }
                }

                return true;
            }
        }
    }

    public Nif(string path)
    {
        _handle = nif_open(path);
        if (_handle == IntPtr.Zero)
            throw new InvalidDataException($"Could not load NIF: {path}");
    }

    public uint StreamVersion => nif_version_stream(_handle);
    public uint BlockCount => nif_block_count(_handle);
    public int ShapeCount => nif_shape_count(_handle);

    public string ShapeName(int index) => ReadString((b, n) => nif_shape_name(_handle, index, b, n));

    public string ShapeBlockType(int index) => ReadString((b, n) => nif_shape_block_type(_handle, index, b, n));

    /// Whether a shape's vertices are weighted to a skeleton. Replacing the geometry of a skinned
    /// shape drops those weights, so this marks a mesh the converter has no business rewriting.
    public bool IsSkinned(int index) => nif_shape_is_skinned(_handle, index) == 1;

    /// How many nodes sit between a shape and the root.
    public int ParentCount(int index) => nif_shape_parent_count(_handle, index);

    /// Level 0 is the immediate parent, counting up towards the root.
    public string ParentName(int index, int level) =>
        ReadString((b, n) => nif_shape_parent_name(_handle, index, level, b, n));

    /// One transform out of the chain. Level -1 is the shape itself, 0 its immediate parent.
    public Transform NodeTransform(int index, int level)
    {
        var translation = new float[3];
        var rotation = new float[9];

        if (nif_shape_transform(_handle, index, level, translation, rotation, out float scale) != 0)
            throw new InvalidOperationException($"No transform at level {level} for shape {index}");

        return new Transform(translation, rotation, scale);
    }

    /// The whole chain composed: what takes a shape's own vertices into world space.
    public Transform WorldTransform(int index)
    {
        var translation = new float[3];
        var rotation = new float[9];

        if (nif_shape_world_transform(_handle, index, translation, rotation, out float scale) != 0)
            throw new InvalidOperationException($"No world transform for shape {index}");

        return new Transform(translation, rotation, scale);
    }

    /// Removes a shape. Indices above it shift down by one, so delete highest-index first.
    public void DeleteShape(int index)
    {
        if (nif_delete_shape(_handle, index) != 0)
            throw new InvalidOperationException($"Could not delete shape {index}");
    }

    /// Draws both faces of a shape, so a translucent gem's open recesses do not read as black holes.
    public void SetDoubleSided(int index) => nif_shape_set_double_sided(_handle, index);

    public bool IsDoubleSided(int index) => nif_shape_double_sided(_handle, index) == 1;

    /// The shape's shader block type, e.g. BSLightingShaderProperty or BSEffectShaderProperty.
    public string ShaderType(int index) => ReadString((b, n) => nif_shape_shader_type(_handle, index, b, n));

    public int VertexCount(int index) => nif_shape_vertex_count(_handle, index);

    public int TriangleCount(int index) => nif_shape_triangle_count(_handle, index);

    /// Slots follow BSShaderTextureSet: 0 diffuse, 1 normal, 4 cubemap, 5 environment mask.
    public string Texture(int index, int slot) => ReadString((b, n) => nif_shape_texture(_handle, index, slot, b, n));

    /// World-space bounds, with the node hierarchy transform applied.
    public (float[] Min, float[] Max, float[] Size) Bounds(int index)
    {
        var min = new float[3];
        var max = new float[3];
        if (nif_shape_bounds(_handle, index, min, max) != 0)
            throw new InvalidOperationException($"No bounds for shape {index}");

        var size = new float[3];
        for (int i = 0; i < 3; i++)
            size[i] = max[i] - min[i];

        return (min, max, size);
    }

    /// Bounds of a shape's own vertices, with no transform applied. Two shapes that share a frame
    /// can only be compared this way: their nodes each put them somewhere else.
    public (float[] Min, float[] Max, float[] Size) LocalBounds(int index)
    {
        var min = new float[3];
        var max = new float[3];
        if (nif_shape_bounds_local(_handle, index, min, max) != 0)
            throw new InvalidOperationException($"No local bounds for shape {index}");

        var size = new float[3];
        for (int i = 0; i < 3; i++)
            size[i] = max[i] - min[i];

        return (min, max, size);
    }

    /// Bounds of just the vertices lying inside a range on one axis, in the shape's own local
    /// space, or null when nothing falls in it. What a shape measures across one stretch of itself,
    /// rather than end to end.
    public (float[] Min, float[] Max, float[] Size)? SliceBounds(int index, int axis, float low, float high)
    {
        var min = new float[3];
        var max = new float[3];

        if (nif_shape_bounds_slice(_handle, index, axis, low, high, min, max) != 0)
            return null;

        var size = new float[3];
        for (int i = 0; i < 3; i++)
            size[i] = max[i] - min[i];

        return (min, max, size);
    }

    public void SetTexture(int index, int slot, string path)
    {
        if (nif_shape_set_texture(_handle, index, slot, path) != 0)
            throw new InvalidOperationException($"Could not set texture slot {slot} on shape {index}");
    }

    /// Gives a shape a texture set of its own, so writing a slot on it leaves the others alone.
    /// Vanilla templates routinely point several shapes at one set, so writing a slot in place would
    /// repaint shapes we never touched. Call once before setting any slot.
    ///
    /// A shape with no texture set to detach is left as it is. An effect shader keeps its paths
    /// inline rather than in a block, so there is nothing to share and nothing to do.
    public void DetachTextures(int index)
    {
        if (nif_shape_detach_textures(_handle, index) < 0)
            throw new InvalidOperationException($"Could not detach the texture set of shape {index}");
    }

    /// Lays a shape onto the surface of others, moving each vertex to the nearest point of their
    /// geometry and lifting it clear by `lift`. A vertex further away than `limit` is left alone,
    /// so a stray one is never dragged across the weapon.
    ///
    /// Everything happens in local space, so the targets must share the shape's own node.
    public void Project(int index, int[] targets, float lift, float limit)
    {
        if (nif_shape_project(_handle, index, targets, targets.Length, lift, limit) < 0)
            throw new InvalidOperationException($"Could not project shape {index}");
    }

    /// Splits triangles whose longest edge exceeds `maxEdgeLength`, so a flat strip gets enough
    /// vertices between its original corners to actually follow a curved surface once projected
    /// onto it. Call before Project, not after: projecting first and subdividing afterwards would
    /// just interpolate along a surface the new vertices were never snapped to.
    public void Subdivide(int index, float maxEdgeLength)
    {
        if (nif_shape_subdivide(_handle, index, maxEdgeLength) != 0)
            throw new InvalidOperationException($"Could not subdivide shape {index}");
    }

    /// Moves a shape's vertices in its own local space: each one is taken relative to `from`,
    /// scaled per axis, and placed relative to `to`. Scaling about a chosen centre rather than the
    /// origin is what lets a blood decal follow a blade that changed both size and position.
    public void Remap(int index,
                      float scaleX, float scaleY, float scaleZ,
                      float fromX, float fromY, float fromZ,
                      float toX, float toY, float toZ)
    {
        if (nif_shape_remap(_handle, index, scaleX, scaleY, scaleZ,
                            fromX, fromY, fromZ, toX, toY, toZ) != 0)
            throw new InvalidOperationException($"Could not remap shape {index}");
    }

    /// Rewrites a shape's vertices into another shape's frame without moving them in the world, and
    /// points the shape's own transform at that frame. A blood decal authored in a node of its own
    /// then shares the weapon's frame, so the local-space steps that follow a decal onto an imported
    /// blade - which need one shared space - work on it too. A decal already in the frame is left
    /// numerically where it was.
    public void Reframe(int index, int frameIndex)
    {
        if (nif_shape_reframe(_handle, index, frameIndex) != 0)
            throw new InvalidOperationException($"Could not reframe shape {index} onto {frameIndex}");
    }

    /// Replaces a shape's geometry with world-space coordinates.
    ///
    /// frameIndex is the shape those coordinates are placed against, normally the target itself.
    /// Pointing it at another shape stores the geometry in that shape's local space instead.
    public void SetGeometry(int index, int frameIndex, float[] positions, float[]? normals,
                            float[]? uvs, int vertexCount, uint[] indices, int triangleCount)
    {
        if (nif_shape_set_geometry(_handle, index, frameIndex, positions, normals, uvs,
                                   vertexCount, indices, triangleCount) != 0)
            throw new InvalidOperationException($"Could not set geometry on shape {index}");
    }

    /// nifly defaults both options to true, and both rewrite more than we asked for. False for a
    /// faithful round trip, true when the engine has to accept the output.
    public void Save(string path, bool optimize, bool sortBlocks)
    {
        int result = nif_save(_handle, path, optimize ? 1 : 0, sortBlocks ? 1 : 0);
        if (result != 0)
            throw new IOException($"nifly failed to save {path} (code {result})");
    }

    private static string ReadString(Func<byte[]?, int, int> call)
    {
        int needed = call(null, 0);
        if (needed <= 1)
            return string.Empty;

        var buffer = new byte[needed];
        call(buffer, needed);
        return Encoding.ASCII.GetString(buffer, 0, needed - 1);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;

        nif_close(_handle);
        _handle = IntPtr.Zero;
    }
}
