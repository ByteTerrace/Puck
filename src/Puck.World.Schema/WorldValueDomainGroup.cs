using System.Numerics;

namespace Puck.World;

/// <summary>Prepared domain metadata for one authored row. Names, source descriptions and tick-zero seeds are
/// retained with the row; live resolution allocates no strings and never reseeds from a later invalid state.</summary>
/// <param name="guard">The consumer's shared transition and work ledger.</param>
/// <param name="definition">The admitted definition whose initial state seeds every field.</param>
/// <param name="path">The row's stable authored path.</param>
public sealed partial class WorldValueDomainGroup(WorldValueDomainGuard guard, WorldDefinition definition, string path) {
    private readonly WorldValueResolver m_initial = new(definition, default);
    private readonly Dictionary<string, Field> m_fields = new(comparer: StringComparer.Ordinal);

    private sealed record Field(string Path, string Source, double Initial, double Second = 0d, string? FirstPath = null, string? SecondPath = null, double? Third = null, string? ThirdPath = null);

    private Field Get(string property, BindableScalar value, double fallback, bool angle = false) {
        if (m_fields.TryGetValue(key: property, value: out var field)) { return field; }
        var initial = (angle ? (float)m_initial.Angle(new(Value: value), fallback) : (float)m_initial.Scalar(fallback: fallback, value: value));

        field = new(((path + ".") + property), WorldValueDomain.SourceOf(value: value), initial);
        m_fields.Add(key: property, value: field);
        return field;
    }

    /// <summary>Resolves and guards an authored scalar, leaving an absent field at its pinned value.</summary>
    /// <param name="property">The field name within this row.</param>
    /// <param name="value">The optional authored operand.</param>
    /// <param name="values">The current shared resolver.</param>
    /// <param name="fallback">The field's pinned value.</param>
    /// <param name="domain">The same numeric domain used at admission.</param>
    /// <returns>The valid, clamped or held value.</returns>
    public float Scalar(string property, BindableScalar? value, WorldValueResolver values, float fallback, WorldValueDomain domain) {
        if (value is not { } scalar) { return fallback; }
        var field = Get(property, scalar, fallback);

        return ((float)guard.Scalar(field.Path, field.Source, ((float)values.Scalar(fallback: fallback, value: scalar)), field.Initial, domain));
    }
    /// <summary>Resolves the short arc of an angle, then applies its admitted numeric domain.</summary>
    /// <param name="property">The field name within this row.</param>
    /// <param name="value">The optional angle.</param>
    /// <param name="values">The current shared resolver.</param>
    /// <param name="fallback">The pinned angle in radians.</param>
    /// <param name="domain">The angle's domain in radians.</param>
    /// <returns>The valid, clamped or held angle.</returns>
    public float Angle(string property, BindableAngle? value, WorldValueResolver values, float fallback, WorldValueDomain domain) {
        if (value is not { } angle) { return fallback; }
        var field = Get(property, angle.Value, fallback, angle: true);

        return ((float)guard.Scalar(field.Path, field.Source, ((float)values.Angle(fallback: fallback, value: angle)), field.Initial, domain));
    }
    /// <summary>Resolves and guards each independently constrained component of an authored pair.</summary>
    /// <param name="property">The field name within this row.</param>
    /// <param name="value">The authored pair or pair keys.</param>
    /// <param name="values">The current shared resolver.</param>
    /// <param name="fallback">The pinned pair.</param>
    /// <param name="domain">The domain of each component.</param>
    /// <returns>The valid, clamped or held components.</returns>
    public Vector2 Pair(string property, BindableVector2 value, WorldValueResolver values, Vector2 fallback, WorldValueDomain domain) {
        if (!m_fields.TryGetValue(key: property, value: out var field)) {
            var seed = m_initial.Vector(fallback: fallback, value: value);
            var source = ((value.Keys is { } keys) ? $"clock {keys.Clock} vector keys"
                : $"({WorldValueDomain.SourceOf(value: value.X)}, {WorldValueDomain.SourceOf(value: value.Y)})");
            var fieldPath = ((path + ".") + property);

            field = new(fieldPath, source, seed.X, seed.Y, (fieldPath + "[0]"), (fieldPath + "[1]"));
            m_fields.Add(key: property, value: field);
        }
        var raw = values.Vector(fallback: fallback, value: value);
        // Component paths are cached with the pair so changing a clock does not allocate diagnostic names.
        return new(x: ((float)guard.Scalar(field.FirstPath!, field.Source, raw.X, field.Initial, domain)),
            y: ((float)guard.Scalar(field.SecondPath!, field.Source, raw.Y, field.Second, domain)));
    }
    /// <summary>Resolves a direction through the shared spherical blend, holding all components for an invalid operand.</summary>
    /// <param name="property">The field name within this row.</param>
    /// <param name="value">The optional direction components or keys.</param>
    /// <param name="values">The current shared resolver.</param>
    /// <param name="fallback">The pinned unit direction used by an absent field.</param>
    /// <returns>The valid or held unit direction.</returns>
    public Vector3 Direction(string property, BindableDirection? value, WorldValueResolver values, Vector3 fallback) {
        if (value is not { } direction) { return fallback; }
        if (!m_fields.TryGetValue(key: property, value: out var field)) {
            var seed = m_initial.Direction(fallback: fallback, value: direction);
            var source = ((direction.Keys is { } keys) ? $"clock {keys.Clock} direction keys"
                : $"({WorldValueDomain.SourceOf(value: direction.Value.X)}, {WorldValueDomain.SourceOf(value: direction.Value.Y)}, {WorldValueDomain.SourceOf(value: direction.Value.Z)})");

            field = new(((path + ".") + property), source, seed.X, seed.Y, Third: seed.Z);
            m_fields.Add(key: property, value: field);
        }
        _ = values.TryDirection(direction: out var resolved, fallback: fallback, value: direction);
        return guard.Direction(field.Path, field.Source, resolved, new(x: ((float)field.Initial), y: ((float)field.Second), z: ((float)field.Third!.Value)));
    }
    /// <summary>Resolves the curvature thresholds together; equality, reversal or non-finite input holds the pair.</summary>
    /// <param name="low">The optional lower operand.</param>
    /// <param name="high">The optional upper operand.</param>
    /// <param name="values">The current shared resolver.</param>
    /// <param name="fallbackLow">The pinned lower value.</param>
    /// <param name="fallbackHigh">The pinned upper value.</param>
    /// <returns>The valid or held pair.</returns>
    public (float Low, float High) Curvature(BindableScalar? low, BindableScalar? high, WorldValueResolver values, float fallbackLow, float fallbackHigh) {
        if ((low is null) && (high is null)) { return (fallbackLow, fallbackHigh); }
        const string Property = "inkLow/inkHigh";

        if (!m_fields.TryGetValue(key: Property, value: out var field)) {
            var a = (low ?? new BindableScalar(literal: fallbackLow));
            var b = (high ?? new BindableScalar(literal: fallbackHigh));

            field = new(((path + ".") + Property), ((WorldValueDomain.SourceOf(value: a) + "/") + WorldValueDomain.SourceOf(value: b)),
                ((float)m_initial.Scalar(fallback: fallbackLow, value: a)), ((float)m_initial.Scalar(fallback: fallbackHigh, value: b)));
            m_fields.Add(key: Property, value: field);
        }
        // The shader consumes binary32 edges: two distinct doubles that narrow to the same edge are invalid too.
        var rawLow = ((low is { } lower) ? (float)values.Scalar(fallback: fallbackLow, value: lower) : fallbackLow);
        var rawHigh = ((high is { } upper) ? (float)values.Scalar(fallback: fallbackHigh, value: upper) : fallbackHigh);
        var used = guard.OrderedPair(field.Path, field.Source, rawLow, rawHigh, field.Initial, field.Second);

        return (((float)used.Low), ((float)used.High));
    }
}
