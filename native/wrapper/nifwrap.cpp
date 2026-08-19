// C surface over nifly. Opaque handles, primitive types, caller-owned buffers: the MSVC C++ ABI is
// not stable and .NET cannot speak it, so nothing else crosses this boundary.

#include <algorithm>
#include <cfloat>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <map>
#include <string>
#include <utility>
#include <vector>

#include "NifFile.hpp"
#include "Shaders.hpp"

#define NIFWRAP_API extern "C" __declspec(dllexport)

namespace {

struct NifHandle {
    nifly::NifFile file;
    std::vector<nifly::NiShape*> shapes;

    void RefreshShapes() { shapes = file.GetShapes(); }
};

// Returns the length the caller needs, so a null buffer is a valid way to ask for the size.
int CopyOut(const std::string& value, char* buffer, int bufferSize) {
    int needed = static_cast<int>(value.size()) + 1;
    if (buffer == nullptr || bufferSize <= 0)
        return needed;

    int copied = bufferSize - 1 < static_cast<int>(value.size()) ? bufferSize - 1 : static_cast<int>(value.size());
    std::memcpy(buffer, value.data(), copied);
    buffer[copied] = '\0';
    return needed;
}

// Ancestors of a block, immediate parent first. A shape is not necessarily registered as a node, so
// this walks the block tree rather than looking anything up by name.
std::vector<nifly::NiNode*> ParentChain(NifHandle* nif, nifly::NiObject* block) {
    std::vector<nifly::NiNode*> chain;

    for (auto* parent = nif->file.GetParentNode(block); parent != nullptr;
         parent = nif->file.GetParentNode(parent)) {
        chain.push_back(parent);
    }

    return chain;
}

// A shape stores its vertices relative to its parent chain, and is not necessarily registered as a
// node, so looking it up by name fails silently and leaves everything in local space.
nifly::MatTransform WorldTransform(NifHandle* nif, nifly::NiShape* shape) {
    nifly::MatTransform toGlobal = shape->GetTransformToParent();

    for (auto* parent : ParentChain(nif, shape)) {
        toGlobal = parent->GetTransformToParent().ComposeTransforms(toGlobal);
    }

    return toGlobal;
}

// Rotation goes out row major, so the caller sees the same layout nifly stores.
void WriteTransform(const nifly::MatTransform& transform,
                    float* outTranslation,
                    float* outRotation,
                    float* outScale) {
    if (outTranslation != nullptr) {
        outTranslation[0] = transform.translation.x;
        outTranslation[1] = transform.translation.y;
        outTranslation[2] = transform.translation.z;
    }

    if (outRotation != nullptr) {
        for (int row = 0; row < 3; row++) {
            outRotation[row * 3] = transform.rotation[row].x;
            outRotation[row * 3 + 1] = transform.rotation[row].y;
            outRotation[row * 3 + 2] = transform.rotation[row].z;
        }
    }

    if (outScale != nullptr)
        *outScale = transform.scale;
}

// Normals carry direction only, so they take the rotation and not the translation or the scale.
nifly::Vector3 RotateNormal(nifly::Matrix3 rotation, const nifly::Vector3& v) {
    nifly::Vector3 r(rotation[0].x * v.x + rotation[0].y * v.y + rotation[0].z * v.z,
                     rotation[1].x * v.x + rotation[1].y * v.y + rotation[1].z * v.z,
                     rotation[2].x * v.x + rotation[2].y * v.y + rotation[2].z * v.z);

    float length = std::sqrt(r.x * r.x + r.y * r.y + r.z * r.z);
    if (length > 1e-6f) {
        r.x /= length;
        r.y /= length;
        r.z /= length;
    }

    return r;
}

nifly::Vector3 Sub(const nifly::Vector3& a, const nifly::Vector3& b) {
    return nifly::Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
}

float Dot(const nifly::Vector3& a, const nifly::Vector3& b) {
    return a.x * b.x + a.y * b.y + a.z * b.z;
}

nifly::Vector3 Cross(const nifly::Vector3& a, const nifly::Vector3& b) {
    return nifly::Vector3(a.y * b.z - a.z * b.y,
                          a.z * b.x - a.x * b.z,
                          a.x * b.y - a.y * b.x);
}

nifly::Vector3 Step(const nifly::Vector3& from, const nifly::Vector3& direction, float t) {
    return nifly::Vector3(from.x + direction.x * t,
                          from.y + direction.y * t,
                          from.z + direction.z * t);
}

// Closest point of a triangle to p, by Voronoi region: the projection when it falls inside, and
// otherwise the nearest point of whichever edge or corner owns the region p sits in.
nifly::Vector3 ClosestOnTriangle(const nifly::Vector3& p,
                                 const nifly::Vector3& a,
                                 const nifly::Vector3& b,
                                 const nifly::Vector3& c) {
    auto ab = Sub(b, a);
    auto ac = Sub(c, a);
    auto ap = Sub(p, a);

    float d1 = Dot(ab, ap);
    float d2 = Dot(ac, ap);
    if (d1 <= 0.0f && d2 <= 0.0f)
        return a;

    auto bp = Sub(p, b);
    float d3 = Dot(ab, bp);
    float d4 = Dot(ac, bp);
    if (d3 >= 0.0f && d4 <= d3)
        return b;

    float vc = d1 * d4 - d3 * d2;
    if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f)
        return Step(a, ab, d1 / (d1 - d3));

    auto cp = Sub(p, c);
    float d5 = Dot(ab, cp);
    float d6 = Dot(ac, cp);
    if (d6 >= 0.0f && d5 <= d6)
        return c;

    float vb = d5 * d2 - d1 * d6;
    if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f)
        return Step(a, ac, d2 / (d2 - d6));

    float va = d3 * d6 - d5 * d4;
    if (va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f)
        return Step(b, Sub(c, b), (d4 - d3) / ((d4 - d3) + (d5 - d6)));

    float denom = 1.0f / (va + vb + vc);
    return nifly::Vector3(a.x + ab.x * (vb * denom) + ac.x * (vc * denom),
                          a.y + ab.y * (vb * denom) + ac.y * (vc * denom),
                          a.z + ab.z * (vb * denom) + ac.z * (vc * denom));
}

nifly::Vector3 Normalized(const nifly::Vector3& v) {
    float length = std::sqrt(Dot(v, v));
    if (length < 1e-8f)
        return nifly::Vector3(0.0f, 0.0f, 0.0f);

    return nifly::Vector3(v.x / length, v.y / length, v.z / length);
}

// One triangle of the surface a decal is laid onto, with the direction it faces and its own box.
struct Facet {
    nifly::Vector3 a;
    nifly::Vector3 b;
    nifly::Vector3 c;
    nifly::Vector3 normal;
    nifly::Vector3 lo;
    nifly::Vector3 hi;
};

// Distance from a point to a box, squared, and zero inside it. A lower bound on the distance to
// anything the box holds, which is all the search needs to drop a face without testing it.
float BoxDistanceSquared(const nifly::Vector3& lo, const nifly::Vector3& hi, const nifly::Vector3& p) {
    float dx = p.x < lo.x ? lo.x - p.x : (p.x > hi.x ? p.x - hi.x : 0.0f);
    float dy = p.y < lo.y ? lo.y - p.y : (p.y > hi.y ? p.y - hi.y : 0.0f);
    float dz = p.z < lo.z ? lo.z - p.z : (p.z > hi.z ? p.z - hi.z : 0.0f);

    return dx * dx + dy * dy + dz * dz;
}

// The triangles of several shapes gathered into one surface, in their shared local space.
//
// A face takes its normal from the authored vertex normals when the shape has them, and only falls
// back to the cross product of its own edges otherwise. Which way a face points outward is what
// decides the side of a blade a decal ends up on, and winding is a convention a file is free to
// have the other way round, so it is read from the data that states it rather than inferred.
std::vector<Facet> CollectSurface(NifHandle* nif, const int* targets, int targetCount, int skip) {
    std::vector<Facet> surface;

    for (int t = 0; t < targetCount; t++) {
        int target = targets[t];
        if (target < 0 || target >= static_cast<int>(nif->shapes.size()) || target == skip)
            continue;

        auto* shape = nif->shapes[target];

        const auto* verts = nif->file.GetVertsForShape(shape);
        if (verts == nullptr || verts->empty())
            continue;

        const auto* normals = nif->file.GetNormalsForShape(shape);
        bool authored = normals != nullptr && normals->size() == verts->size();

        std::vector<nifly::Triangle> tris;
        if (!shape->GetTriangles(tris))
            continue;

        for (const auto& tri : tris) {
            if (tri.p1 >= verts->size() || tri.p2 >= verts->size() || tri.p3 >= verts->size())
                continue;

            Facet facet;
            facet.a = (*verts)[tri.p1];
            facet.b = (*verts)[tri.p2];
            facet.c = (*verts)[tri.p3];

            if (authored) {
                const auto& n1 = (*normals)[tri.p1];
                const auto& n2 = (*normals)[tri.p2];
                const auto& n3 = (*normals)[tri.p3];

                facet.normal = Normalized(nifly::Vector3(n1.x + n2.x + n3.x,
                                                         n1.y + n2.y + n3.y,
                                                         n1.z + n2.z + n3.z));
            }

            if (Dot(facet.normal, facet.normal) < 0.5f)
                facet.normal = Normalized(Cross(Sub(facet.b, facet.a), Sub(facet.c, facet.a)));

            // A degenerate face has no side to lay anything on.
            if (Dot(facet.normal, facet.normal) < 0.5f)
                continue;

            facet.lo = nifly::Vector3(std::min({facet.a.x, facet.b.x, facet.c.x}),
                                      std::min({facet.a.y, facet.b.y, facet.c.y}),
                                      std::min({facet.a.z, facet.b.z, facet.c.z}));

            facet.hi = nifly::Vector3(std::max({facet.a.x, facet.b.x, facet.c.x}),
                                      std::max({facet.a.y, facet.b.y, facet.c.y}),
                                      std::max({facet.a.z, facet.b.z, facet.c.z}));

            surface.push_back(facet);
        }
    }

    return surface;
}

} // namespace

NIFWRAP_API void* nif_open(const char* path) {
    if (path == nullptr)
        return nullptr;

    auto* handle = new NifHandle();
    if (handle->file.Load(std::filesystem::path(path)) != 0 || !handle->file.IsValid()) {
        delete handle;
        return nullptr;
    }

    handle->RefreshShapes();
    return handle;
}

// optimize and sortBlocks mirror nifly's NifSaveOptions, which default to true. Both rewrite more
// than we asked for, so they stay explicit: false for a faithful round trip, true for engine output.
NIFWRAP_API int nif_save(void* handle, const char* path, int optimize, int sortBlocks) {
    if (handle == nullptr || path == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);

    nifly::NifSaveOptions options;
    options.optimize = optimize != 0;
    options.sortBlocks = sortBlocks != 0;

    return nif->file.Save(std::filesystem::path(path), options);
}

NIFWRAP_API void nif_close(void* handle) {
    delete static_cast<NifHandle*>(handle);
}

NIFWRAP_API unsigned int nif_version_stream(void* handle) {
    if (handle == nullptr)
        return 0;
    return static_cast<NifHandle*>(handle)->file.GetHeader().GetVersion().Stream();
}

NIFWRAP_API unsigned int nif_block_count(void* handle) {
    if (handle == nullptr)
        return 0;
    return static_cast<NifHandle*>(handle)->file.GetHeader().GetNumBlocks();
}

NIFWRAP_API int nif_shape_count(void* handle) {
    if (handle == nullptr)
        return -1;
    return static_cast<int>(static_cast<NifHandle*>(handle)->shapes.size());
}

// Turns on double-sided rendering for a shape's shader. Oblivion models its glass and amber gems as
// translucent shells with recessed facets and no walls closing them, relying on the see-through
// material. Converted to an opaque single-sided shape the recess back-faces are culled and read as a
// black hole; drawing both sides fills it with the inner facet instead.
NIFWRAP_API int nif_shape_set_double_sided(void* handle, int index) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* shader = nif->file.GetShader(nif->shapes[index]);
    if (shader == nullptr)
        return 1;

    shader->SetDoubleSided(true);
    return 0;
}

// The block type of a shape's shader, e.g. BSLightingShaderProperty for a weapon or
// BSEffectShaderProperty for a glow/tracer overlay. Empty when the shape has no shader.
NIFWRAP_API int nif_shape_shader_type(void* handle, int index, char* buffer, int bufferSize) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* shader = nif->file.GetShader(nif->shapes[index]);
    return CopyOut(shader != nullptr ? shader->GetBlockName() : "", buffer, bufferSize);
}

// 1 when a shape's shader draws both faces. Used to confirm the double-sided flag survived the save.
NIFWRAP_API int nif_shape_double_sided(void* handle, int index) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* shader = nif->file.GetShader(nif->shapes[index]);
    return shader != nullptr && shader->IsDoubleSided() ? 1 : 0;
}

// Removes a shape and its shader/geometry from the file. Used to drop a template's glow overlay,
// which is cut to the vanilla weapon's silhouette and hangs off the imported one. Shape indices
// shift afterwards, so callers delete highest-first.
NIFWRAP_API int nif_delete_shape(void* handle, int index) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    nif->file.DeleteShape(nif->shapes[index]);
    nif->RefreshShapes();
    return 0;
}

NIFWRAP_API int nif_shape_name(void* handle, int index, char* buffer, int bufferSize) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    return CopyOut(nif->shapes[index]->name.get(), buffer, bufferSize);
}

NIFWRAP_API int nif_shape_block_type(void* handle, int index, char* buffer, int bufferSize) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    return CopyOut(nif->shapes[index]->GetBlockName(), buffer, bufferSize);
}

// Whether a shape's vertices are weighted to a skeleton. Replacing the geometry of a skinned shape
// drops those weights, so this is what separates a mesh we can convert from one we cannot.
NIFWRAP_API int nif_shape_is_skinned(void* handle, int index) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    return nif->shapes[index]->IsSkinned() ? 1 : 0;
}

// How many nodes sit between a shape and the root. Zero means the shape hangs directly off nothing
// the file exposes as a parent.
NIFWRAP_API int nif_shape_parent_count(void* handle, int index) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    return static_cast<int>(ParentChain(nif, nif->shapes[index]).size());
}

// Level 0 is the immediate parent, counting up towards the root.
NIFWRAP_API int nif_shape_parent_name(void* handle, int index, int level, char* buffer, int bufferSize) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto chain = ParentChain(nif, nif->shapes[index]);
    if (level < 0 || level >= static_cast<int>(chain.size()))
        return -1;

    return CopyOut(chain[level]->name.get(), buffer, bufferSize);
}

// One transform out of the chain, relative to its own parent. Level -1 is the shape itself, 0 its
// immediate parent, and so on up. Rotation is nine floats row major.
NIFWRAP_API int nif_shape_transform(void* handle,
                                    int index,
                                    int level,
                                    float* outTranslation,
                                    float* outRotation,
                                    float* outScale) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    if (level == -1) {
        WriteTransform(nif->shapes[index]->GetTransformToParent(), outTranslation, outRotation, outScale);
        return 0;
    }

    auto chain = ParentChain(nif, nif->shapes[index]);
    if (level < 0 || level >= static_cast<int>(chain.size()))
        return -1;

    WriteTransform(chain[level]->GetTransformToParent(), outTranslation, outRotation, outScale);
    return 0;
}

// The whole chain composed: what takes a shape's own vertices into world space.
NIFWRAP_API int nif_shape_world_transform(void* handle,
                                          int index,
                                          float* outTranslation,
                                          float* outRotation,
                                          float* outScale) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    WriteTransform(WorldTransform(nif, nif->shapes[index]), outTranslation, outRotation, outScale);
    return 0;
}

NIFWRAP_API int nif_shape_vertex_count(void* handle, int index) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    const auto* vertices = nif->file.GetVertsForShape(nif->shapes[index]);
    return vertices == nullptr ? 0 : static_cast<int>(vertices->size());
}

NIFWRAP_API int nif_shape_triangle_count(void* handle, int index) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    return static_cast<int>(nif->shapes[index]->GetNumTriangles());
}

// Slots follow BSShaderTextureSet: 0 diffuse, 1 normal, 4 cubemap, 5 environment mask.
NIFWRAP_API int nif_shape_texture(void* handle, int index, int slot, char* buffer, int bufferSize) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    std::string texture;
    nif->file.GetTextureSlot(nif->shapes[index], texture, static_cast<uint32_t>(slot));
    return CopyOut(texture, buffer, bufferSize);
}

NIFWRAP_API int nif_shape_set_texture(void* handle, int index, int slot, const char* path) {
    if (handle == nullptr || path == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    std::string texture(path);
    nif->file.SetTextureSlot(nif->shapes[index], texture, static_cast<uint32_t>(slot));
    return 0;
}

// Gives a shape its own copy of its texture set. Vanilla templates often point several shapes at one
// BSShaderTextureSet, so writing a slot in place would repaint them all - call this once first.
//
// Returns 1 when there's nothing to detach, which isn't a failure: an effect shader keeps its paths
// inline and has no set to share. Always clones rather than testing for sharing; the stray block
// left on an unshared set is a few dozen bytes.
NIFWRAP_API int nif_shape_detach_textures(void* handle, int index) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* shader = nif->file.GetShader(nif->shapes[index]);
    if (shader == nullptr)
        return 1;

    auto* ref = shader->TextureSetRef();
    if (ref == nullptr || ref->IsEmpty())
        return 1;

    auto& header = nif->file.GetHeader();

    auto* current = header.GetBlock<nifly::BSShaderTextureSet>(ref);
    if (current == nullptr)
        return 1;

    ref->index = header.AddBlock(current->Clone());
    return 0;
}

// World-space bounds of a shape, as six floats: min xyz then max xyz.
NIFWRAP_API int nif_shape_bounds(void* handle, int index, float* outMin, float* outMax) {
    if (handle == nullptr || outMin == nullptr || outMax == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* shape = nif->shapes[index];
    const auto* verts = nif->file.GetVertsForShape(shape);
    if (verts == nullptr || verts->empty())
        return -1;

    nifly::MatTransform toGlobal = WorldTransform(nif, shape);

    nifly::Vector3 lo(FLT_MAX, FLT_MAX, FLT_MAX);
    nifly::Vector3 hi(-FLT_MAX, -FLT_MAX, -FLT_MAX);

    for (const auto& v : *verts) {
        auto p = toGlobal.ApplyTransform(v);
        lo.x = std::min(lo.x, p.x);
        lo.y = std::min(lo.y, p.y);
        lo.z = std::min(lo.z, p.z);
        hi.x = std::max(hi.x, p.x);
        hi.y = std::max(hi.y, p.y);
        hi.z = std::max(hi.z, p.z);
    }

    outMin[0] = lo.x; outMin[1] = lo.y; outMin[2] = lo.z;
    outMax[0] = hi.x; outMax[1] = hi.y; outMax[2] = hi.z;
    return 0;
}

// Bounds of a shape's own vertices, untransformed, as six floats: min xyz then max xyz.
//
// The world bounds answer where a shape sits; these answer how it is modelled. Comparing two shapes
// that share a frame, such as an arrow and the cut-down copies of it standing in a quiver, only
// works on the untransformed geometry, since each copy's node moves it somewhere else.
NIFWRAP_API int nif_shape_bounds_local(void* handle, int index, float* outMin, float* outMax) {
    if (handle == nullptr || outMin == nullptr || outMax == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    const auto* verts = nif->file.GetVertsForShape(nif->shapes[index]);
    if (verts == nullptr || verts->empty())
        return -1;

    nifly::Vector3 lo(FLT_MAX, FLT_MAX, FLT_MAX);
    nifly::Vector3 hi(-FLT_MAX, -FLT_MAX, -FLT_MAX);

    for (const auto& v : *verts) {
        lo.x = std::min(lo.x, v.x);
        lo.y = std::min(lo.y, v.y);
        lo.z = std::min(lo.z, v.z);
        hi.x = std::max(hi.x, v.x);
        hi.y = std::max(hi.y, v.y);
        hi.z = std::max(hi.z, v.z);
    }

    outMin[0] = lo.x; outMin[1] = lo.y; outMin[2] = lo.z;
    outMax[0] = hi.x; outMax[1] = hi.y; outMax[2] = hi.z;
    return 0;
}

// Bounds of just the vertices whose local coordinate on one axis falls inside a range, as six
// floats: min xyz then max xyz. Returns 1 when the slab holds nothing.
//
// A weapon's overall box is set by whatever sticks out furthest, which on a sword is the crossguard.
// A blood decal lies on the blade. Asking what the shape measures across one stretch of its own
// length is the only way to scale the decal by the blade rather than by the guard.
NIFWRAP_API int nif_shape_bounds_slice(void* handle,
                                       int index,
                                       int axis,
                                       float low,
                                       float high,
                                       float* outMin,
                                       float* outMax) {
    if (handle == nullptr || outMin == nullptr || outMax == nullptr)
        return -1;
    if (axis < 0 || axis > 2)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    const auto* verts = nif->file.GetVertsForShape(nif->shapes[index]);
    if (verts == nullptr || verts->empty())
        return -1;

    nifly::Vector3 lo(FLT_MAX, FLT_MAX, FLT_MAX);
    nifly::Vector3 hi(-FLT_MAX, -FLT_MAX, -FLT_MAX);
    bool found = false;

    for (const auto& v : *verts) {
        float along = axis == 0 ? v.x : axis == 1 ? v.y : v.z;
        if (along < low || along > high)
            continue;

        found = true;
        lo.x = std::min(lo.x, v.x);
        lo.y = std::min(lo.y, v.y);
        lo.z = std::min(lo.z, v.z);
        hi.x = std::max(hi.x, v.x);
        hi.y = std::max(hi.y, v.y);
        hi.z = std::max(hi.z, v.z);
    }

    if (!found)
        return 1;

    outMin[0] = lo.x; outMin[1] = lo.y; outMin[2] = lo.z;
    outMax[0] = hi.x; outMax[1] = hi.y; outMax[2] = hi.z;
    return 0;
}

// Snaps every vertex of a shape onto the target surface and lifts it clear by a hair. This is what
// puts a blood decal on an imported blade: a decal is a flat strip authored on one weapon, so
// scaling alone leaves it floating over a blade of a different taper - only snapping ends that.
//
// No vertex is dropped by distance. `limit` instead decides when the face a vertex already faces
// beats the nearest one: on a thin blade the front and back faces are barely apart, so taking the
// nearest outright would punch half the strip through to the back.
//
// Everything is in local space, so targets must share the shape's own node.
NIFWRAP_API int nif_shape_project(void* handle,
                                  int index,
                                  const int* targets,
                                  int targetCount,
                                  float lift,
                                  float limit) {
    if (handle == nullptr || targets == nullptr || targetCount <= 0)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* decal = nif->shapes[index];
    const auto* points = nif->file.GetVertsForShape(decal);
    if (points == nullptr || points->empty())
        return -1;

    auto surface = CollectSurface(nif, targets, targetCount, index);
    if (surface.empty())
        return 1;

    std::vector<nifly::Vector3> moved(*points);

    const auto* normalsIn = nif->file.GetNormalsForShape(decal);
    bool hasNormals = normalsIn != nullptr && normalsIn->size() == points->size();

    std::vector<nifly::Vector3> authored;
    if (hasNormals)
        authored = *normalsIn;

    std::vector<nifly::Vector3> facing = authored;

    const float reach = limit > 0.0f ? limit * limit : FLT_MAX;

    for (size_t i = 0; i < points->size(); i++) {
        const auto& p = (*points)[i];

        auto side = hasNormals ? Normalized(authored[i]) : nifly::Vector3();
        bool oriented = Dot(side, side) > 0.5f;

        size_t nearest = SIZE_MAX;
        size_t ahead = SIZE_MAX;
        float nearestDistance = FLT_MAX;
        float aheadDistance = FLT_MAX;
        nifly::Vector3 nearestLanding;
        nifly::Vector3 aheadLanding;

        for (size_t f = 0; f < surface.size(); f++) {
            const auto& facet = surface[f];
            bool faces = oriented && Dot(facet.normal, side) > 0.0f;

            float bound = BoxDistanceSquared(facet.lo, facet.hi, p);
            if (bound >= nearestDistance && !(faces && bound < aheadDistance))
                continue;

            auto landing = ClosestOnTriangle(p, facet.a, facet.b, facet.c);
            auto gap = Sub(p, landing);
            float distance = Dot(gap, gap);

            if (distance < nearestDistance) {
                nearestDistance = distance;
                nearestLanding = landing;
                nearest = f;
            }

            if (faces && distance < aheadDistance) {
                aheadDistance = distance;
                aheadLanding = landing;
                ahead = f;
            }
        }

        size_t chosen = nearest;
        auto landing = nearestLanding;

        if (ahead != SIZE_MAX && (aheadDistance <= reach || nearestDistance > reach)) {
            chosen = ahead;
            landing = aheadLanding;
        }

        if (chosen == SIZE_MAX)
            continue;

        auto normal = surface[chosen].normal;
        float away = 1.0f;

        if (oriented)
            away = Dot(normal, side) < 0.0f ? -1.0f : 1.0f;
        else if (Dot(Sub(p, landing), normal) < 0.0f)
            away = -1.0f;

        moved[i] = Step(landing, normal, lift * away);

        if (hasNormals)
            facing[i] = nifly::Vector3(normal.x * away, normal.y * away, normal.z * away);
    }

    nif->file.SetVertsForShape(decal, moved);

    if (hasNormals)
        nif->file.SetNormalsForShape(decal, facing);

    decal->UpdateBounds();
    nif->file.CalcTangentsForShape(decal);

    return 0;
}

// Splits triangles whose longest edge exceeds a length, giving a decal strip enough vertices to
// follow a curved blade once nif_shape_project snaps them down - otherwise only its corners land
// and everything between floats.
//
// Several passes, since a triangle twice too big still exceeds the limit after one split. Each pass
// keys new midpoints on the (lower, higher) index pair so triangles sharing an edge share one
// midpoint, or the split would crack along the seam.
//
// Vertex colours ride along with the rest: nifly drops the other vertex streams on a count change,
// and a decal fades out through its vertex alpha, so losing it would leave a strip that can't fade.
NIFWRAP_API int nif_shape_subdivide(void* handle, int index, float maxEdgeLength) {
    if (handle == nullptr || maxEdgeLength <= 0.0f)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* shape = nif->shapes[index];

    const auto* vertsIn = nif->file.GetVertsForShape(shape);
    if (vertsIn == nullptr || vertsIn->empty())
        return -1;

    std::vector<nifly::Triangle> tris;
    if (!shape->GetTriangles(tris) || tris.empty())
        return -1;

    std::vector<nifly::Vector3> verts(*vertsIn);

    const auto* normalsIn = nif->file.GetNormalsForShape(shape);
    bool hasNormals = normalsIn != nullptr && normalsIn->size() == verts.size();
    std::vector<nifly::Vector3> norms;
    if (hasNormals)
        norms = *normalsIn;

    const auto* uvsIn = nif->file.GetUvsForShape(shape);
    bool hasUvs = uvsIn != nullptr && uvsIn->size() == verts.size();
    std::vector<nifly::Vector2> uvs;
    if (hasUvs)
        uvs = *uvsIn;

    const auto* colorsIn = nif->file.GetColorsForShape(shape);
    bool hasColors = colorsIn != nullptr && colorsIn->size() == verts.size();
    std::vector<nifly::Color4> colors;
    if (hasColors)
        colors = *colorsIn;

    const float limit = maxEdgeLength * maxEdgeLength;
    const int maxPasses = 6;

    // A decal only needs enough vertices to follow a blade, and this geometry ships inside a weapon
    // mesh. Skyrim indices are 16 bit, so the ceiling is 65535 either way.
    const size_t vertexCap = 20000;
    const size_t triangleCap = 40000;

    for (int pass = 0; pass < maxPasses; pass++) {
        std::map<std::pair<uint16_t, uint16_t>, uint16_t> midpoints;
        std::vector<nifly::Triangle> next;
        next.reserve(tris.size());
        bool split = false;

        auto MidOf = [&](uint16_t a, uint16_t b) -> uint16_t {
            auto key = a < b ? std::make_pair(a, b) : std::make_pair(b, a);
            auto found = midpoints.find(key);
            if (found != midpoints.end())
                return found->second;

            verts.push_back(nifly::Vector3((verts[a].x + verts[b].x) * 0.5f,
                                           (verts[a].y + verts[b].y) * 0.5f,
                                           (verts[a].z + verts[b].z) * 0.5f));

            if (hasNormals)
                norms.push_back(Normalized(nifly::Vector3((norms[a].x + norms[b].x) * 0.5f,
                                                          (norms[a].y + norms[b].y) * 0.5f,
                                                          (norms[a].z + norms[b].z) * 0.5f)));

            if (hasUvs) {
                uvs.push_back(nifly::Vector2((uvs[a].u + uvs[b].u) * 0.5f,
                                             (uvs[a].v + uvs[b].v) * 0.5f));
            }

            if (hasColors) {
                colors.push_back(nifly::Color4((colors[a].r + colors[b].r) * 0.5f,
                                               (colors[a].g + colors[b].g) * 0.5f,
                                               (colors[a].b + colors[b].b) * 0.5f,
                                               (colors[a].a + colors[b].a) * 0.5f));
            }

            uint16_t added = static_cast<uint16_t>(verts.size() - 1);
            midpoints[key] = added;
            return added;
        };

        for (const auto& tri : tris) {
            if (verts.size() >= vertexCap || next.size() >= triangleCap) {
                next.push_back(tri);
                continue;
            }

            const auto& a = verts[tri.p1];
            const auto& b = verts[tri.p2];
            const auto& c = verts[tri.p3];

            float ab = Dot(Sub(a, b), Sub(a, b));
            float bc = Dot(Sub(b, c), Sub(b, c));
            float ca = Dot(Sub(c, a), Sub(c, a));

            if (ab <= limit && bc <= limit && ca <= limit) {
                next.push_back(tri);
                continue;
            }

            split = true;
            uint16_t mab = MidOf(tri.p1, tri.p2);
            uint16_t mbc = MidOf(tri.p2, tri.p3);
            uint16_t mca = MidOf(tri.p3, tri.p1);

            next.push_back(nifly::Triangle(tri.p1, mab, mca));
            next.push_back(nifly::Triangle(mab, tri.p2, mbc));
            next.push_back(nifly::Triangle(mca, mbc, tri.p3));
            next.push_back(nifly::Triangle(mab, mbc, mca));
        }

        tris = std::move(next);
        if (!split)
            break;
    }

    nif->file.SetVertsForShape(shape, verts);
    shape->SetTriangles(tris);

    if (hasUvs)
        nif->file.SetUvsForShape(shape, uvs);

    if (hasNormals)
        nif->file.SetNormalsForShape(shape, norms);

    if (hasColors)
        nif->file.SetColorsForShape(shape, colors);

    shape->UpdateBounds();
    nif->file.CalcTangentsForShape(shape);

    nif->RefreshShapes();
    return 0;
}

// Replaces a shape's geometry. Positions, normals and uvs are flat arrays over the same vertex
// count; indices are triangle corners. Coordinates are expected in Skyrim world space.
//
// frameIndex is the shape whose placement the coordinates are read against, and is normally the
// target itself. Pointing it elsewhere stores the geometry in that other shape's local space, which
// is how the spare arrows of a quiver each keep the placement their own node gives them.
//
// Skinned shapes are out of scope: the caller filters them out, since replacing their vertices
// would throw away bone weights this has no way to rebuild.
NIFWRAP_API int nif_shape_set_geometry(void* handle,
                                       int index,
                                       int frameIndex,
                                       const float* positions,
                                       const float* normals,
                                       const float* uvs,
                                       int vertexCount,
                                       const unsigned int* indices,
                                       int triangleCount) {
    if (handle == nullptr || positions == nullptr || indices == nullptr)
        return -1;
    if (vertexCount <= 0 || triangleCount <= 0)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;
    if (frameIndex < 0 || frameIndex >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* shape = nif->shapes[index];

    // Undo the parent transform, or the engine applies it a second time and the mesh comes out
    // squashed or stretched depending on the template.
    nifly::MatTransform toLocal = WorldTransform(nif, nif->shapes[frameIndex]).InverseTransform();

    std::vector<nifly::Vector3> verts(vertexCount);
    std::vector<nifly::Vector3> norms(vertexCount);
    std::vector<nifly::Vector2> texCoords(vertexCount);

    for (int i = 0; i < vertexCount; i++) {
        verts[i] = toLocal.ApplyTransform(
            nifly::Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]));

        if (normals != nullptr) {
            norms[i] = RotateNormal(
                toLocal.rotation,
                nifly::Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]));
        }

        if (uvs != nullptr)
            texCoords[i] = nifly::Vector2(uvs[i * 2], uvs[i * 2 + 1]);
    }

    std::vector<nifly::Triangle> tris(triangleCount);
    for (int i = 0; i < triangleCount; i++) {
        tris[i] = nifly::Triangle(static_cast<uint16_t>(indices[i * 3]),
                                  static_cast<uint16_t>(indices[i * 3 + 1]),
                                  static_cast<uint16_t>(indices[i * 3 + 2]));
    }

    nif->file.SetVertsForShape(shape, verts);
    shape->SetTriangles(tris);

    if (uvs != nullptr)
        nif->file.SetUvsForShape(shape, texCoords);

    if (normals != nullptr)
        nif->file.SetNormalsForShape(shape, norms);

    shape->UpdateBounds();
    nif->file.CalcTangentsForShape(shape);

    nif->RefreshShapes();
    return 0;
}

// Remaps a shape's vertices in its own local space: v' = (v - from) * scale + to.
//
// This is how the blood decals follow the imported weapon. A decal is a flat strip modelled against
// the vanilla blade, so it only stays on the edge if it is stretched by however much the blade
// changed in each direction and then moved to where the blade ended up. Scaling about the old
// centre rather than about the origin is what keeps it from sliding off along the way.
NIFWRAP_API int nif_shape_remap(void* handle,
                                int index,
                                float scaleX, float scaleY, float scaleZ,
                                float fromX, float fromY, float fromZ,
                                float toX, float toY, float toZ) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* shape = nif->shapes[index];
    const auto* current = nif->file.GetVertsForShape(shape);
    if (current == nullptr || current->empty())
        return -1;

    std::vector<nifly::Vector3> moved(current->size());
    for (size_t i = 0; i < current->size(); i++) {
        const auto& v = (*current)[i];
        moved[i] = nifly::Vector3((v.x - fromX) * scaleX + toX,
                                  (v.y - fromY) * scaleY + toY,
                                  (v.z - fromZ) * scaleZ + toZ);
    }

    nif->file.SetVertsForShape(shape, moved);
    shape->UpdateBounds();

    return 0;
}

// Rewrites a shape's vertices into another shape's frame without moving them in the world, and
// repoints its transform so rendering is unchanged.
//
// A blood decal is often authored in its own node. The decal-following steps all work in one shared
// local space, so a decal in a separate frame can't be measured against the weapon; bringing it into
// the weapon's frame first is what lets the same machinery handle it.
NIFWRAP_API int nif_shape_reframe(void* handle, int index, int frameIndex) {
    if (handle == nullptr)
        return -1;

    auto* nif = static_cast<NifHandle*>(handle);
    if (index < 0 || index >= static_cast<int>(nif->shapes.size()))
        return -1;
    if (frameIndex < 0 || frameIndex >= static_cast<int>(nif->shapes.size()))
        return -1;

    auto* shape = nif->shapes[index];
    const auto* verts = nif->file.GetVertsForShape(shape);
    if (verts == nullptr || verts->empty())
        return -1;

    nifly::MatTransform decalWorld = WorldTransform(nif, shape);
    nifly::MatTransform frameWorld = WorldTransform(nif, nif->shapes[frameIndex]);

    // v_new such that frameWorld(v_new) == decalWorld(v_old): the world position is untouched.
    nifly::MatTransform toNew = frameWorld.InverseTransform().ComposeTransforms(decalWorld);

    std::vector<nifly::Vector3> moved(verts->size());
    for (size_t i = 0; i < verts->size(); i++)
        moved[i] = toNew.ApplyTransform((*verts)[i]);

    const auto* normals = nif->file.GetNormalsForShape(shape);
    if (normals != nullptr && normals->size() == verts->size()) {
        std::vector<nifly::Vector3> turned(normals->size());
        for (size_t i = 0; i < normals->size(); i++)
            turned[i] = Normalized(toNew.ApplyTransformToDir((*normals)[i]));
        nif->file.SetNormalsForShape(shape, turned);
    }

    nif->file.SetVertsForShape(shape, moved);

    // Point the shape's own transform-to-parent so its world transform becomes frameWorld. With
    // parentChain the composition of everything above the shape, world = parentChain ∘ self, so
    // self = parentChain^-1 ∘ frameWorld, and parentChain = decalWorld ∘ self_old^-1.
    nifly::MatTransform selfOld = shape->GetTransformToParent();
    nifly::MatTransform parentChain = decalWorld.ComposeTransforms(selfOld.InverseTransform());
    shape->SetTransformToParent(parentChain.InverseTransform().ComposeTransforms(frameWorld));

    shape->UpdateBounds();
    nif->file.CalcTangentsForShape(shape);

    return 0;
}
