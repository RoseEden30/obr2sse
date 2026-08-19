using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Obr2Sse;

/// A tiny software rasteriser for eyeballing converted geometry without a game or a NIF viewer.
/// Orthographic, Gouraud-shaded from the stored vertex normals, open edges drawn red so holes and
/// stray lines show. Used by the render and contact-sheet diagnostics.
public static class MeshRaster
{
    /// Renders one merged primitive into a WxH colour buffer (transparent background), fit and centred.
    /// view: 0 looks down x, 1 down y, 2 down z.
    public static Rgba32[] Render(MeshPrimitive m, int view, int width, int height, int pad = 10)
    {
        var col = new Rgba32[width * height];
        var z = new float[width * height];
        Array.Fill(z, float.MaxValue);

        if (m.Indices.Length == 0)
            return col;

        int ax = view == 0 ? 1 : 0;
        int ay = view == 2 ? 1 : 2;

        float Comp(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

        var (bmin, bmax) = m.Bounds();
        float minU = Comp(bmin, ax), minV = Comp(bmin, ay);
        float spanU = MathF.Max(Comp(bmax, ax) - minU, 1e-3f);
        float spanV = MathF.Max(Comp(bmax, ay) - minV, 1e-3f);

        float scale = MathF.Min((width - 2 * pad) / spanU, (height - 2 * pad) / spanV);
        float offU = (width - spanU * scale) / 2f;
        float offV = (height - spanV * scale) / 2f;

        (int x, int y, float d) Proj(Vector3 v) =>
            ((int)(offU + (Comp(v, ax) - minU) * scale),
             (int)(height - offV - (Comp(v, ay) - minV) * scale),
             Comp(v, view));

        var p = m.Positions;
        var nrm = m.Normals;

        // Cull mode drops triangles whose vertex normals face away from the camera, the way a single-
        // sided shader does in game - so a hole with no front face reads black instead of showing the
        // far wall through it. This is what the game actually does; the default no-cull view hides it.
        bool cull = Environment.GetEnvironmentVariable("OBR2SSE_CULL") == "1";

        float ShadeOf(int vi, Vector3 faceN)
        {
            var n = vi < nrm.Length && nrm[vi].LengthSquared() > 1e-6f ? nrm[vi] : faceN;
            return 0.20f + 0.80f * MathF.Abs(Comp(Vector3.Normalize(n), view));
        }

        Vector3 SafeNormal(int vi) =>
            vi < nrm.Length && nrm[vi].LengthSquared() > 1e-6f ? Vector3.Normalize(nrm[vi]) : Vector3.Zero;

        (long, long, long) Q(Vector3 v) =>
            ((long)MathF.Round(v.X * 50f), (long)MathF.Round(v.Y * 50f), (long)MathF.Round(v.Z * 50f));
        var edges = new Dictionary<((long, long, long), (long, long, long)), int>();
        void CountEdge(Vector3 a, Vector3 b)
        {
            var qa = Q(a); var qb = Q(b);
            var k = qa.CompareTo(qb) < 0 ? (qa, qb) : (qb, qa);
            edges[k] = edges.GetValueOrDefault(k) + 1;
        }

        for (int i = 0; i < m.Indices.Length; i += 3)
        {
            int ia = (int)m.Indices[i], ib = (int)m.Indices[i + 1], ic = (int)m.Indices[i + 2];
            Vector3 a = p[ia], b = p[ib], c = p[ic];
            CountEdge(a, b); CountEdge(b, c); CountEdge(c, a);
            var faceN = Vector3.Normalize(Vector3.Cross(b - a, c - a));

            // Single-sided cull: drop triangles whose vertex normals point away from the camera. The
            // camera looks along +view (nearest = smallest depth), so a face toward it has a negative
            // view component. A hole with only an inward-facing far wall then reads black.
            if (cull)
            {
                var vn = SafeNormal(ia) + SafeNormal(ib) + SafeNormal(ic);
                if (Comp(vn, view) > 0f)
                    continue;
            }

            Tri(Proj(a), Proj(b), Proj(c), ShadeOf(ia, faceN), ShadeOf(ib, faceN), ShadeOf(ic, faceN), col, z, width, height);
        }

        foreach (var e in edges.Where(e => (e.Value & 1) == 1))
        {
            var (qa, qb) = e.Key;
            var pa = Proj(new Vector3(qa.Item1 / 50f, qa.Item2 / 50f, qa.Item3 / 50f));
            var pb = Proj(new Vector3(qb.Item1 / 50f, qb.Item2 / 50f, qb.Item3 / 50f));
            Line(pa.x, pa.y, pb.x, pb.y, new Rgba32(255, 40, 40, 255), col, width, height);
        }

        return col;
    }

    private static void Tri((int x, int y, float d) A, (int x, int y, float d) B, (int x, int y, float d) C,
        float sA, float sB, float sC, Rgba32[] col, float[] z, int W, int H)
    {
        int minX = Math.Max(0, Math.Min(A.x, Math.Min(B.x, C.x)));
        int maxX = Math.Min(W - 1, Math.Max(A.x, Math.Max(B.x, C.x)));
        int minY = Math.Max(0, Math.Min(A.y, Math.Min(B.y, C.y)));
        int maxY = Math.Min(H - 1, Math.Max(A.y, Math.Max(B.y, C.y)));
        float area = (B.x - A.x) * (C.y - A.y) - (B.y - A.y) * (C.x - A.x);
        if (MathF.Abs(area) < 1e-3f) return;

        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            float w0 = ((B.x - x) * (C.y - y) - (B.y - y) * (C.x - x)) / area;
            float w1 = ((C.x - x) * (A.y - y) - (C.y - y) * (A.x - x)) / area;
            float w2 = 1 - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            float d = w0 * A.d + w1 * B.d + w2 * C.d;
            int idx = y * W + x;
            if (d < z[idx])
            {
                z[idx] = d;
                byte g = (byte)Math.Clamp((w0 * sA + w1 * sB + w2 * sC) * 255f, 15f, 255f);
                col[idx] = new Rgba32(g, g, g, 255);
            }
        }
    }

    private static void Line(int x0, int y0, int x1, int y1, Rgba32 c, Rgba32[] col, int W, int H)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            if (x0 >= 0 && x0 < W && y0 >= 0 && y0 < H) col[y0 * W + x0] = c;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
