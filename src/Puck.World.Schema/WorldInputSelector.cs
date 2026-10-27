using System.Globalization;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>A selector's proven finite partition. Evaluation uses authoritative values and allocates nothing.</summary>
public sealed class WorldInputSelector {
    private readonly Dictionary<string, ushort> m_labels = new(StringComparer.Ordinal);
    private readonly Dictionary<long, ushort> m_values = [];
    private readonly (long Minimum, long Maximum, ushort State)[] m_ranges;

    private WorldInputSelector(WorldInputModeFamily family, WorldDefinition definition) {
        Kind = family.Selector.Kind;
        Name = family.Selector.Name;
        var ranges = new List<(long Minimum, long Maximum, ushort State)>();
        for (var state = 0; state < family.States.Count; state++) {
            var row = family.States[state];
            if (row.Name == family.Default) { Default = (ushort)state; }
            foreach (var value in row.Values ?? []) {
                var added = Kind == WorldInputModeSelectorKind.BodyState
                    ? m_values.TryAdd(Parse(value).Value, (ushort)state)
                    : m_labels.TryAdd(value, (ushort)state);
                if (!added) { throw Invalid(family, $"selector value '{value}' belongs to several states"); }
            }
            foreach (var range in row.Ranges ?? []) {
                if (Kind != WorldInputModeSelectorKind.BodyState) { throw Invalid(family, "ranges require a BodyState selector"); }
                var minimum = Parse(range.Minimum.ToString(CultureInfo.InvariantCulture)).Value;
                var maximum = Parse(range.Maximum.ToString(CultureInfo.InvariantCulture)).Value;
                if (minimum > maximum) { throw Invalid(family, $"state '{row.Name}' has a reversed range"); }
                ranges.Add((minimum, maximum, (ushort)state));
            }
        }
        m_ranges = ranges.OrderBy(static row => row.Minimum).ToArray();
        for (var index = 1; index < m_ranges.Length; index++) {
            if (m_ranges[index].Minimum <= m_ranges[index - 1].Maximum) { throw Invalid(family, "selector ranges overlap"); }
        }
        switch (Kind) {
            case WorldInputModeSelectorKind.ActiveKit:
                if (Name is not null) { throw Invalid(family, "ActiveKit takes no register name"); }
                RequireLabels(family, definition.Kits.Select(static kit => kit.Name));
                break;
            case WorldInputModeSelectorKind.Engagement:
                if (Name is not null) { throw Invalid(family, "Engagement takes no register name"); }
                RequireLabels(family, ["none", "tabletop", "machine", "screen", "body"]);
                break;
            case WorldInputModeSelectorKind.SeatState:
                var seatFamily = definition.SeatModes.FirstOrDefault(row => row.Name == Name);
                if (seatFamily is null) { throw Invalid(family, $"unknown seat state family '{Name}'"); }
                RequireLabels(family, seatFamily.States.Select(static row => row.Name));
                break;
            case WorldInputModeSelectorKind.BodyState:
                var register = definition.BodyState.Concat(definition.IdentityState).FirstOrDefault(row => row.Name == Name);
                if (register is null) { throw Invalid(family, $"unknown body state '{Name}'"); }
                if (register.Kind != ActionStateKind.Counter) { throw Invalid(family, $"body state '{Name}' must be a counter"); }
                if ((m_ranges.Length != 0) && (m_values.Count != 0)) { throw Invalid(family, "use either finite values or ranges, not both"); }
                switch (register.Envelope) {
                    case ActionStateEnvelope.Set set:
                        var envelope = set.Values.Select(static value => FixedQ4816.FromDouble(value).Value).ToHashSet();
                        if (m_ranges.Length != 0 || !envelope.SetEquals(m_values.Keys)) { throw Invalid(family, $"values must exactly partition '{Name}' envelope"); }
                        break;
                    case ActionStateEnvelope.Range range:
                        var minimum = FixedQ4816.FromDouble(range.Minimum).Value;
                        var maximum = FixedQ4816.FromDouble(range.Maximum).Value;
                        if (m_ranges.Length == 0 || m_ranges[0].Minimum != minimum || m_ranges[^1].Maximum != maximum) {
                            throw Invalid(family, $"ranges must exhaust '{Name}' envelope [{range.Minimum}, {range.Maximum}]");
                        }
                        for (var index = 1; index < m_ranges.Length; index++) {
                            if ((Int128)m_ranges[index].Minimum - m_ranges[index - 1].Maximum != 1) { throw Invalid(family, $"ranges leave a gap in '{Name}' envelope"); }
                        }
                        break;
                    default:
                        throw Invalid(family, $"body state '{Name}' needs a finite envelope");
                }
                break;
            default:
                throw Invalid(family, $"unknown selector kind '{Kind}'");
        }
    }

    /// <summary>Which authoritative source supplies values.</summary>
    public WorldInputModeSelectorKind Kind { get; }
    /// <summary>The register or seat-state family to read, if applicable.</summary>
    public string? Name { get; }
    /// <summary>Selection only when the owner has no selector subject.</summary>
    public ushort Default { get; }
    /// <summary>Compiles and proves this partition over the document's selector envelope.</summary>
    public static WorldInputSelector Compile(WorldInputModeFamily family, WorldDefinition definition) => new(family, definition);
    /// <summary>Selects a declared label; an unknown value refuses instead of choosing the default.</summary>
    public bool TrySelect(string value, out ushort state) => m_labels.TryGetValue(value, out state);
    /// <summary>Selects a fixed value against the precompiled finite partition.</summary>
    public bool TrySelect(FixedQ4816 value, out ushort state) {
        if (m_values.TryGetValue(value.Value, out state)) { return true; }
        foreach (var range in m_ranges) {
            if (value.Value >= range.Minimum && value.Value <= range.Maximum) { state = range.State; return true; }
        }
        state = 0;
        return false;
    }
    private void RequireLabels(WorldInputModeFamily family, IEnumerable<string> expected) {
        var values = expected.ToHashSet(StringComparer.Ordinal);
        if (!values.SetEquals(m_labels.Keys)) { throw Invalid(family, $"values must exactly partition [{string.Join(", ", values.Order(StringComparer.Ordinal))}]"); }
    }
    private static FixedQ4816 Parse(string value) => FixedQ4816.Parse(value, CultureInfo.InvariantCulture);
    private static ArgumentException Invalid(WorldInputModeFamily family, string detail) => new($"inputModes family '{family.Name}': {detail}");
}
