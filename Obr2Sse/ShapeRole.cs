using System.Text.RegularExpressions;

namespace Obr2Sse;

/// What a shape in a vanilla template is for.
public enum ShapeRole
{
    /// The weapon itself, replaced with the imported geometry.
    Weapon,

    /// The sheath, shown when the weapon is stowed.
    Scabbard,

    /// Flat decal strips laid along the blade, stretched to follow it.
    Blood,

    /// Effect geometry cut to the vanilla silhouette, left alone.
    Glow,

    /// A tracer or trail drawn by an effect shader, left alone.
    Effect,

    /// Nothing we recognise. Reported rather than touched.
    Unknown,
}

/// Vanilla templates name their shapes by convention rather than by any flag in the file, so the
/// name is most of what there is to go on. Anything outside the convention is left Unknown so it
/// surfaces instead of being quietly mangled.
///
/// Order matters. The weapon suffix is the loosest rule and has to come last: EdgeBlood17:0 is a
/// decal, not geometry.
public static partial class ShapeRoles
{
    /// The weapon's own geometry, split by material: GlassSword01:1, GlassSword01:2.
    [GeneratedRegex(@":\d+$")]
    private static partial Regex WeaponSuffix { get; }

    public static ShapeRole Classify(string name)
    {
        if (name.Contains("Blood", StringComparison.OrdinalIgnoreCase))
            return ShapeRole.Blood;

        if (name.Contains("Glow", StringComparison.OrdinalIgnoreCase))
            return ShapeRole.Glow;

        // Sheaths are Scb everywhere in the vanilla set, with no variant spelling.
        if (name.StartsWith("Scb", StringComparison.OrdinalIgnoreCase))
            return ShapeRole.Scabbard;

        if (WeaponSuffix.IsMatch(name))
            return ShapeRole.Weapon;

        return ShapeRole.Unknown;
    }

    /// The same, with what the file itself says taken into account.
    ///
    /// Some effect geometry is named like weapon material sections and the suffix accepts it. What
    /// separates them is not the name: a tracer or glow is drawn by an effect shader and has no
    /// texture set at all, where a weapon always has a diffuse.
    public static ShapeRole Classify(Nif nif, int index)
    {
        var role = Classify(nif.ShapeName(index));

        // A glow, tracer, or plume is drawn by an effect shader whatever it is named - DaggerEffect01,
        // BattleAxeGlow1st. Blood decals use one too, but they are caught by name above and kept; any
        // other unrecognised shape on an effect shader is an overlay cut to the vanilla silhouette.
        if (role is ShapeRole.Unknown && nif.ShaderType(index) == "BSEffectShaderProperty")
            return ShapeRole.Effect;

        if (role == ShapeRole.Weapon && nif.Texture(index, 0).Length == 0)
            return ShapeRole.Effect;

        return role;
    }

    /// Indices of every shape in a loaded NIF playing a given role, in file order.
    public static List<int> Find(Nif nif, ShapeRole role)
    {
        var result = new List<int>();

        for (int i = 0; i < nif.ShapeCount; i++)
        {
            if (Classify(nif, i) == role)
                result.Add(i);
        }

        return result;
    }
}
