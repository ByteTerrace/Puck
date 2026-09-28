using System.Numerics;
using Puck.Hosting;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>The live values a presentation supplies to the pure document resolver. The schema has no knowledge of
/// clients or mirrors; an absent source reads the document's authored state.</summary>
public interface IWorldValueSource {
    /// <summary>Reads a binding's presented numeric value.</summary>
    /// <param name="binding">The parsed cell reference.</param>
    /// <param name="value">The numeric value when present.</param>
    /// <returns>Whether the source has a numeric value for this binding.</returns>
    bool TryScalar(in StateBinding binding, out double value);
    /// <summary>Reads a binding's presented linear color, including alpha.</summary>
    /// <param name="binding">The parsed cell reference.</param>
    /// <param name="value">The color when present.</param>
    /// <returns>Whether the source has a color for this binding.</returns>
    bool TryColor(in StateBinding binding, out Vector4 value);
}

/// <summary>Optional observation of the clocks a resolve actually reads, used by presentation caches.</summary>
public interface IWorldValueTrace {
    /// <summary>Records a clock and its resolved phase.</summary>
    /// <param name="clock">The clock read.</param>
    /// <param name="phase">Its normalized phase.</param>
    void ReadClock(WorldClock clock, double phase);
}

/// <summary>The one presentation-value resolver: clock phases, key selection, easing and typed blends. It carries no
/// history, so a seek or a frozen capture resolves the same values as any other presentation of that tick.</summary>
/// <param name="Timeline">The presentation clocks.</param>
/// <param name="Tick">The tick being presented.</param>
/// <param name="Source">The live value source, or null to read authored state as the validator does.</param>
/// <param name="Definition">The document available only for authored-state validation; live presentation needs no document.</param>
/// <param name="Trace">Optional clock-read observation for a presentation cache.</param>
public readonly record struct WorldValueResolver(WorldTimelineSection Timeline, PresentedTick Tick, IWorldValueSource? Source = null, WorldDefinition? Definition = null, IWorldValueTrace? Trace = null) {
    /// <summary>Creates a resolver over a document, including its authored-state fallback.</summary>
    /// <param name="definition">The authored world.</param>
    /// <param name="tick">The presented tick.</param>
    /// <param name="source">An optional live value source.</param>
    public WorldValueResolver(WorldDefinition definition, PresentedTick tick, IWorldValueSource? source = null) : this(definition.Timeline, tick, source, definition) { }
    /// <summary>Finds a named clock without creating another clock table.</summary>
    /// <param name="name">The authored clock name.</param>
    /// <returns>The named clock, or null.</returns>
    public WorldClock? Clock(string name) {
        var clocks = Timeline.Clocks;
        if (clocks is not null) {
            for (var index = 0; index < clocks.Count; index++) {
                if (string.Equals(clocks[index].Name, name, StringComparison.Ordinal)) {
                    return clocks[index];
                }
            }
        }
        return null;
    }

    /// <summary>Resolves a clock's normalized phase.</summary>
    /// <param name="clock">A validated clock.</param>
    /// <returns>The phase in [0, 1).</returns>
    public double Phase(WorldClock clock) {
        double resolved;
        if (clock.Phase is { } phase) {
            resolved = WorldClocks.Phase(Scalar(phase, 0d));
        } else if (clock.State is { } row) {
            resolved = WorldClocks.Phase(ReadScalar(new StateBinding(row, null, false), 0d));
        } else {
            resolved = WorldClocks.Phase(clock, Tick);
        }
        Trace?.ReadClock(clock, resolved);
        return resolved;
    }

    /// <summary>Resolves a scalar, keeping the source's double precision until its consumer chooses a narrower type.</summary>
    /// <param name="value">The literal, binding or keyed value.</param>
    /// <param name="fallback">The value used when a live binding is absent.</param>
    /// <returns>The resolved scalar.</returns>
    public double Scalar(in BindableScalar value, double fallback) {
        if (value.Keys is { } keys && Segment(keys, out var from, out var to, out var weight)) {
            var a = Scalar(from.Value, fallback);
            return a + ((Scalar(to.Value, fallback) - a) * weight);
        }
        return value.State is { } binding ? ReadScalar(binding, fallback)
            : value.Literal is { } literal && float.IsFinite(literal) ? literal : fallback;
    }

    /// <summary>Resolves a linear color. Hex channels already name linear values; alpha blends independently.</summary>
    /// <param name="value">The literal, binding or keyed color.</param>
    /// <param name="fallback">The value used when a live binding is absent.</param>
    /// <returns>The resolved color.</returns>
    public Vector4 Color(in BindableColor value, Vector4 fallback) {
        if (value.Keys is { } keys && Segment(keys, out var from, out var to, out var weight)) {
            return Vector4.Lerp(Color(from.Value, fallback), Color(to.Value, fallback), (float)weight);
        }
        if (value.State is { } binding) {
            if (Source is { } source) {
                return source.TryColor(in binding, out var color) ? color : fallback;
            }
            return Definition is { } definition && WorldStateReader.TryRead(definition, binding.Row, binding.Key, 0UL, Tick.Whole, out _, out _, out var text)
                && HexColor.TryParseRgba(text, out var authored) ? authored : fallback;
        }
        return value.Literal ?? fallback;
    }

    /// <summary>Resolves an angle in radians along the short arc.</summary>
    /// <param name="value">The authored angle.</param>
    /// <param name="fallback">The angle used when a binding is absent.</param>
    /// <returns>The resolved angle, in radians.</returns>
    public double Angle(in BindableAngle value, double fallback) {
        if (value.Value.Keys is { } keys && Segment(keys, out var from, out var to, out var weight)) {
            var a = Angle(new BindableAngle(from.Value), fallback);
            var b = Angle(new BindableAngle(to.Value), fallback);
            return a + (Math.IEEERemainder(b - a, Math.Tau) * weight);
        }
        return Scalar(value.Value, fallback);
    }

    /// <summary>Resolves a unit direction along the great circle, with a deterministic arc for antipodal endpoints.</summary>
    /// <param name="value">The authored direction.</param>
    /// <param name="fallback">The direction used when an operand is absent.</param>
    /// <returns>The normalized direction.</returns>
    public Vector3 Direction(in BindableDirection value, Vector3 fallback) => TryDirection(value, fallback, out var direction) ? direction : fallback;

    /// <summary>Resolves a direction without hiding an invalid component vector behind a fallback.</summary>
    /// <param name="value">The authored direction.</param>
    /// <param name="fallback">The direction used when an operand is absent.</param>
    /// <param name="direction">The normalized direction, or the invalid component vector when resolution fails.</param>
    /// <returns>Whether every active direction operand is finite and nonzero.</returns>
    public bool TryDirection(in BindableDirection value, Vector3 fallback, out Vector3 direction) {
        if (value.Keys is { } keys && Segment(keys, out var from, out var to, out var weight)) {
            var fromValid = TryDirection(from.Value, fallback, out var a);
            var toValid = TryDirection(to.Value, fallback, out var b);
            if (!fromValid || !toValid) { direction = fromValid ? b : a; return false; }
            direction = DirectionArc(a, b, (float)weight);
            return true;
        }
        return TryUnit(Vector(value.Value, fallback), out direction);
    }

    /// <summary>Resolves a vector by blending its components linearly.</summary>
    /// <param name="value">The authored vector.</param>
    /// <param name="fallback">The vector used when an operand is absent.</param>
    /// <returns>The resolved vector.</returns>
    public Vector3 Vector(in BindableVector3 value, Vector3 fallback) {
        if (value.Keys is { } keys && Segment(keys, out var from, out var to, out var weight)) {
            return Vector3.Lerp(Vector(from.Value, fallback), Vector(to.Value, fallback), (float)weight);
        }
        return new Vector3((float)Scalar(value.X, fallback.X), (float)Scalar(value.Y, fallback.Y), (float)Scalar(value.Z, fallback.Z));
    }

    /// <summary>Resolves a pair by blending its components linearly.</summary>
    /// <param name="value">The authored pair.</param>
    /// <param name="fallback">The pair used when an operand is absent.</param>
    /// <returns>The resolved pair.</returns>
    public Vector2 Vector(in BindableVector2 value, Vector2 fallback) {
        if (value.Keys is { } keys && Segment(keys, out var from, out var to, out var weight)) {
            return Vector2.Lerp(Vector(from.Value, fallback), Vector(to.Value, fallback), (float)weight);
        }
        return new Vector2((float)Scalar(value.X, fallback.X), (float)Scalar(value.Y, fallback.Y));
    }

    private double ReadScalar(StateBinding binding, double fallback) {
        if (Source is { } source) {
            return source.TryScalar(in binding, out var value) ? value : fallback;
        }
        if (Definition is not { } definition || !WorldStateReader.TryRead(definition, binding.Row, binding.Key, 0UL, Tick.Whole, out var row, out var raw, out _) || raw is not { } bits) {
            return fallback;
        }
        return row.Kind switch {
            CellKind.Int => bits,
            CellKind.Fixed => (double)Puck.Maths.FixedQ4816.FromRawBits(bits),
            _ => fallback,
        };
    }

    private bool Segment<T>(WorldKeys<T> curve, out WorldKey<T> from, out WorldKey<T> to, out double weight) {
        from = to = null!;
        weight = 0d;
        if (curve.Keys.Count < 2 || Clock(curve.Clock) is not { } clock) {
            return false;
        }
        var at = Phase(clock) * clock.Span;
        WorldKeyInterpolation.Select(curve, clock.Span, at, out from, out to, out var duration, out var elapsed);
        var t = Math.Clamp(elapsed / duration, 0d, 1d);
        weight = WorldKeyInterpolation.Ease(from.Ease, t);
        return true;
    }

    private static Vector3 Unit(Vector3 value, Vector3 fallback) => TryUnit(value, out var unit) ? unit : fallback;

    private static bool TryUnit(Vector3 value, out Vector3 unit) {
        double x = value.X, y = value.Y, z = value.Z;
        var length = Math.Sqrt((x * x) + (y * y) + (z * z));
        var valid = length > 0d && double.IsFinite(length);
        unit = valid ? new Vector3((float)(x / length), (float)(y / length), (float)(z / length)) : value;
        return valid;
    }

    private static Vector3 DirectionArc(Vector3 a, Vector3 b, float weight) {
        var dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (dot > 0.9995f) { return Unit(Vector3.Lerp(a, b, weight), a); }
        var tangent = b - (dot * a);
        if (tangent.LengthSquared() < 1e-12f) {
            var axis = MathF.Abs(a.X) < MathF.Abs(a.Y) ? Vector3.UnitX : Vector3.UnitY;
            if (MathF.Abs(a.Z) < MathF.Abs(Vector3.Dot(a, axis))) { axis = Vector3.UnitZ; }
            tangent = Vector3.Cross(a, axis);
        }
        var angle = MathF.Acos(dot) * weight;
        return (a * MathF.Cos(angle)) + (Vector3.Normalize(tangent) * MathF.Sin(angle));
    }
}
