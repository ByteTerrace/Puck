using System.Numerics;

namespace Puck.World;

public sealed partial class WorldValueDomainGroup {
    /// <summary>Resolves independently constrained vector components. Unlike a direction, a position or offset
    /// keeps its magnitude; an invalid component holds only that component's last presented value.</summary>
    /// <param name="property">The authored field name.</param>
    /// <param name="value">The optional vector or vector keys.</param>
    /// <param name="values">The shared presentation resolver.</param>
    /// <param name="fallback">The absent vector.</param>
    /// <param name="domain">The domain of each component.</param>
    /// <returns>The valid, clamped or held components.</returns>
    public Vector3 Vector(string property, BindableVector3? value, WorldValueResolver values, Vector3 fallback, WorldValueDomain domain) {
        if (value is not { } vector) { return fallback; }
        if (!m_fields.TryGetValue(property, out var field)) {
            var seed = m_initial.Vector(vector, fallback);
            var source = vector.Keys is { } keys ? $"clock {keys.Clock} vector keys"
                : $"({WorldValueDomain.SourceOf(vector.X)}, {WorldValueDomain.SourceOf(vector.Y)}, {WorldValueDomain.SourceOf(vector.Z)})";
            var fieldPath = path + "." + property;
            field = new(fieldPath, source, seed.X, seed.Y, fieldPath + "[0]", fieldPath + "[1]", seed.Z, fieldPath + "[2]");
            m_fields.Add(property, field);
        }
        var raw = values.Vector(vector, fallback);
        return new((float)guard.Scalar(field.FirstPath!, field.Source, raw.X, field.Initial, domain),
            (float)guard.Scalar(field.SecondPath!, field.Source, raw.Y, field.Second, domain),
            (float)guard.Scalar(field.ThirdPath!, field.Source, raw.Z, field.Third!.Value, domain));
    }
}
