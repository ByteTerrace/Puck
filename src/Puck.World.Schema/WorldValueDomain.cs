using System.Numerics;

namespace Puck.World;

/// <summary>A presentation field's numeric domain, shared by admission and live resolution. Closed bounds clamp;
/// non-finite values and violations of open bounds retain the last valid value.</summary>
/// <param name="Minimum">The lower bound, or negative infinity.</param>
/// <param name="Maximum">The upper bound, or positive infinity.</param>
/// <param name="MinimumOpen">Whether equality with the lower bound is invalid.</param>
/// <param name="MaximumOpen">Whether equality with the upper bound is invalid.</param>
public readonly record struct WorldValueDomain(double Minimum = double.NegativeInfinity,
    double Maximum = double.PositiveInfinity, bool MinimumOpen = false, bool MaximumOpen = false) {
    /// <summary>Every finite value.</summary>
    public static WorldValueDomain Finite { get; } = new(double.NegativeInfinity, double.PositiveInfinity);
    /// <summary>Finite nonnegative values.</summary>
    public static WorldValueDomain Nonnegative { get; } = new(0d, double.PositiveInfinity);
    /// <summary>Finite strictly positive values.</summary>
    public static WorldValueDomain Positive { get; } = new(0d, double.PositiveInfinity, MinimumOpen: true);
    /// <summary>The closed unit interval.</summary>
    public static WorldValueDomain Unit { get; } = new(0d, 1d);

    /// <summary>Tests the field's authored or resolved value against the same domain.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns>Whether it is finite and inside every bound.</returns>
    public bool Contains(double value) => double.IsFinite(value)
        && (MinimumOpen ? value > Minimum : value >= Minimum)
        && (MaximumOpen ? value < Maximum : value <= Maximum);

    /// <summary>Returns whether this invalid value requires holding, rather than a closed-bound clamp.</summary>
    /// <param name="value">The resolved value.</param>
    /// <returns>Whether the value is non-finite or violates an open bound.</returns>
    public bool RequiresHold(double value) => !double.IsFinite(value)
        || (MinimumOpen && value <= Minimum) || (MaximumOpen && value >= Maximum);

    /// <summary>Describes a scalar's authored source once when a consumer prepares its field metadata.</summary>
    /// <param name="value">The field's authored operand.</param>
    /// <returns>The literal, state token or clock and its key sources.</returns>
    public static string SourceOf(BindableScalar value) => value.Binding ?? (value.Keys is { } keys
        ? $"clock {keys.Clock} keys ({string.Join(", ", keys.Keys.Select(static key => SourceOf(key.Value)))})"
        : value.Literal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent");

    /// <summary>Describes the numeric interval for a named diagnostic.</summary>
    /// <returns>The finite interval, using parentheses for open bounds.</returns>
    public override string ToString() => FormattableString.Invariant($"{(MinimumOpen ? '(' : '[')}{Minimum}, {Maximum}{(MaximumOpen ? ')' : ']')} (finite)");
}

/// <summary>One transition into an invalid field or back to valid input. A pair diagnostic names both components.</summary>
/// <param name="Field">The authored field or coupled pair.</param>
/// <param name="Source">Its literal, binding or clock-key source.</param>
/// <param name="Value">The newly observed first value.</param>
/// <param name="Used">The first value actually presented.</param>
/// <param name="Domain">The field or pair's requirement.</param>
/// <param name="Action">The transition: clamped, held, or recovered.</param>
/// <param name="SecondValue">The newly observed second value for a pair.</param>
/// <param name="SecondUsed">The second value actually presented for a pair.</param>
/// <param name="ThirdValue">The newly observed third component for a direction.</param>
/// <param name="ThirdUsed">The third component actually presented for a direction.</param>
public readonly record struct WorldValueDomainDiagnostic(string Field, string Source, double Value, double Used,
    string Domain, string Action, double? SecondValue = null, double? SecondUsed = null, double? ThirdValue = null, double? ThirdUsed = null) {
    /// <summary>Formats the named transition for the shared inspector and console.</summary>
    /// <returns>The field, source, observed values, domain and values used.</returns>
    public override string ToString() => ThirdValue is { } third
        ? FormattableString.Invariant($"{Field} from {Source}: ({Value}, {SecondValue}, {third}) requires {Domain}; {Action} ({Used}, {SecondUsed}, {ThirdUsed})")
        : SecondValue is { } second
        ? FormattableString.Invariant($"{Field} from {Source}: ({Value}, {second}) requires {Domain}; {Action} ({Used}, {SecondUsed})")
        : FormattableString.Invariant($"{Field} from {Source}: {Value} requires {Domain}; {Action} {Used}");
}

/// <summary>The shared presentation domain guard. A consumer seeds each field with its admitted tick-zero value,
/// retains only current field state, and reports transitions rather than repeating diagnostics every frame.</summary>
public sealed class WorldValueDomainGuard {
    private readonly Dictionary<string, Entry> m_fields = new(StringComparer.Ordinal);
    private readonly List<WorldValueDomainDiagnostic> m_diagnostics = [];
    private sealed class Entry(double first, double second, double third) {
        public double Last = first;
        public double LastSecond = second;
        public double LastThird = third;
        public double Raw;
        public double RawSecond;
        public double RawThird;
        public double Used;
        public double UsedSecond;
        public double UsedThird;
        public bool Seen;
        public bool Invalid;
        public string? Domain;
        public WorldValueDomainDiagnostic? Diagnostic;
    }

    /// <summary>Reports only transitions into invalid input and back to valid input.</summary>
    public event Action<WorldValueDomainDiagnostic>? Transition;
    /// <summary>Gets the active invalid-field diagnostics; storage is reused after transitions.</summary>
    public IReadOnlyList<WorldValueDomainDiagnostic> Diagnostics => m_diagnostics;
    /// <summary>Gets changed-input domain checks actually performed.</summary>
    public long Checks { get; private set; }
    /// <summary>Gets changed inputs clamped to closed bounds.</summary>
    public long Clamps { get; private set; }
    /// <summary>Gets changed inputs that retained their last valid field or coupled pair.</summary>
    public long Holds { get; private set; }

    /// <summary>Starts a new admitted definition/revision while preserving cumulative work counts.</summary>
    public void Reset() { m_fields.Clear(); m_diagnostics.Clear(); }

    /// <summary>Checks one scalar only when its resolved input changed.</summary>
    /// <param name="field">The stable authored field path.</param>
    /// <param name="source">The source spelling used by diagnostics.</param>
    /// <param name="value">The current raw resolved value.</param>
    /// <param name="initial">The admitted value resolved at tick zero.</param>
    /// <param name="domain">The field's domain.</param>
    /// <returns>The valid, clamped or held value.</returns>
    public double Scalar(string field, string source, double value, double initial, WorldValueDomain domain) {
        var entry = Get(field, initial, 0d);
        if (entry.Seen && entry.Raw.Equals(value)) { return entry.Used; }
        Checks++;
        var invalid = !domain.Contains(value);
        var hold = invalid && domain.RequiresHold(value);
        var used = hold ? entry.Last : invalid ? Math.Clamp(value, domain.Minimum, domain.Maximum) : value;
        if (invalid) { if (hold) { Holds++; } else { Clamps++; } }
        if (!hold) { entry.Last = used; }
        entry.Raw = value;
        entry.Used = used;
        entry.Seen = true;
        if (invalid || entry.Invalid) {
            entry.Domain ??= domain.ToString();
            Changed(entry, invalid, new(field, source, value, used, entry.Domain, invalid ? hold ? "held" : "clamped" : "recovered"));
        }
        return used;
    }

    /// <summary>Checks a coupled strictly ordered nonnegative pair; a violation holds both last valid values.</summary>
    /// <param name="field">The pair's authored path.</param>
    /// <param name="source">Both source spellings.</param>
    /// <param name="low">The current lower value.</param>
    /// <param name="high">The current upper value.</param>
    /// <param name="initialLow">The admitted tick-zero lower value.</param>
    /// <param name="initialHigh">The admitted tick-zero upper value.</param>
    /// <returns>The valid or held pair.</returns>
    public (double Low, double High) OrderedPair(string field, string source, double low, double high, double initialLow, double initialHigh) {
        var entry = Get(field, initialLow, initialHigh);
        if (entry.Seen && entry.Raw.Equals(low) && entry.RawSecond.Equals(high)) { return (entry.Used, entry.UsedSecond); }
        Checks++;
        var invalid = !double.IsFinite(low) || !double.IsFinite(high) || low < 0d || low >= high;
        if (invalid) { Holds++; }
        else { entry.Last = low; entry.LastSecond = high; }
        entry.Raw = low;
        entry.RawSecond = high;
        entry.Used = entry.Last;
        entry.UsedSecond = entry.LastSecond;
        entry.Seen = true;
        Changed(entry, invalid, new(field, source, low, entry.Used, "finite 0 <= low < high", invalid ? "held" : "recovered", high, entry.UsedSecond));
        return (entry.Used, entry.UsedSecond);
    }

    /// <summary>Accepts a resolved unit direction, or holds all components when its raw operand was invalid.</summary>
    /// <param name="field">The direction's authored path.</param>
    /// <param name="source">Its component or key sources.</param>
    /// <param name="value">The resolved unit direction, or the invalid raw vector returned by the shared resolver.</param>
    /// <param name="initial">The admitted tick-zero unit direction.</param>
    /// <returns>The valid or held unit direction.</returns>
    public Vector3 Direction(string field, string source, Vector3 value, Vector3 initial) {
        var entry = Get(field, initial.X, initial.Y, initial.Z);
        if (entry.Seen && entry.Raw.Equals((double)value.X) && entry.RawSecond.Equals((double)value.Y) && entry.RawThird.Equals((double)value.Z)) {
            return new((float)entry.Used, (float)entry.UsedSecond, (float)entry.UsedThird);
        }
        Checks++;
        var invalid = !float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || value == Vector3.Zero;
        if (invalid) { Holds++; }
        else { entry.Last = value.X; entry.LastSecond = value.Y; entry.LastThird = value.Z; }
        entry.Raw = value.X;
        entry.RawSecond = value.Y;
        entry.RawThird = value.Z;
        entry.Used = entry.Last;
        entry.UsedSecond = entry.LastSecond;
        entry.UsedThird = entry.LastThird;
        entry.Seen = true;
        Changed(entry, invalid, new(field, source, value.X, entry.Used, "finite nonzero direction", invalid ? "held" : "recovered",
            value.Y, entry.UsedSecond, value.Z, entry.UsedThird));
        return new((float)entry.Used, (float)entry.UsedSecond, (float)entry.UsedThird);
    }

    private Entry Get(string field, double initial, double second, double third = 0d) {
        if (m_fields.TryGetValue(field, out var entry)) { return entry; }
        entry = new(initial, second, third);
        m_fields.Add(field, entry);
        return entry;
    }
    private void Changed(Entry entry, bool invalid, WorldValueDomainDiagnostic diagnostic) {
        var transition = entry.Invalid != invalid;
        if (!transition && !invalid) { return; }
        entry.Invalid = invalid;
        entry.Diagnostic = invalid ? diagnostic : null;
        m_diagnostics.Clear();
        foreach (var value in m_fields.Values) {
            if (value.Diagnostic is { } active) { m_diagnostics.Add(active); }
        }
        if (transition) { Transition?.Invoke(diagnostic); }
    }
}
