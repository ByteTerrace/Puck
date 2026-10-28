using System.Globalization;
using Puck.Maths;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A bounded semantic history pattern. Adjacent steps with one group name match in any order.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputPattern(string Name, IReadOnlyList<WorldInputPatternStep> Steps,
    decimal MaximumDurationSeconds = 0.5m, decimal MaximumGapSeconds = 0.25m,
    ulong? BufferTicks = null, IReadOnlyList<string>? Held = null, string? ConsumptionGroup = null);
/// <summary>One event predicate. Tick gaps refer to the preceding stage's admitted tick.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputPatternStep(string Channel, WorldInputEventKind Kind = WorldInputEventKind.Press,
    long? RawValue = null, string? Crossing = null, string? SimultaneousGroup = null,
    decimal ToleranceSeconds = 0, ulong MinimumTickGap = 0, ulong? MaximumTickGap = null);
/// <summary>A pattern's compiled event predicate.</summary>
public readonly record struct WorldInputPatternTerm(ushort Channel, WorldInputEventKind Kind, long? RawValue, int Crossing);
/// <summary>An ordered stage, containing one event or several independent simultaneous predicates.</summary>
public sealed record WorldInputPatternStage(WorldInputPatternTerm[] Terms, FixedQ4816 Tolerance, ulong MinimumTicks, ulong? MaximumTicks);
/// <summary>Immutable bounded matching program. No physical source or genre appears in its vocabulary.</summary>
public sealed class CompiledWorldInputPattern {
    /// <summary>Declared pattern identity.</summary>
    public required string Name { get; init; }
    /// <summary>Ordered stages.</summary>
    public required WorldInputPatternStage[] Stages { get; init; }
    /// <summary>Maximum first-to-last event duration.</summary>
    public required FixedQ4816 Duration { get; init; }
    /// <summary>Maximum gap between adjacent stages.</summary>
    public required FixedQ4816 Gap { get; init; }
    /// <summary>Exact admission-tick buffer, if authored.</summary>
    public ulong? BufferTicks { get; init; }
    /// <summary>Required currently held binary actions.</summary>
    public required ushort[] Held { get; init; }
    /// <summary>Shared consumption group, or this pattern's private group.</summary>
    public required int Group { get; init; }
    /// <summary>Total selected events.</summary>
    public int Width => Stages.Sum(selector: static stage => stage.Terms.Length);

    /// <summary>Conservative work bound for this program, including all earlier shared-group priorities.</summary>
    public static long Work(IReadOnlyList<CompiledWorldInputPattern> patterns, int index, int capacity) {
        var work = 0L;

        for (var prior = 0; (prior <= index); prior++) {
            if (patterns[prior].Group != patterns[index].Group) { continue; }
            var pattern = patterns[prior];
            // Every predecessor/end pair may search sixteen times, compare a full path, and test a tick bound.
            work = checked(((work + (((((long)capacity) * capacity) * pattern.Stages.Length) * (32 + (4 * pattern.Width)))) + ((capacity * pattern.Width) * 4L)));
        }
        return work;
    }
    /// <summary>Compiles logical names, bounded stages, disjoint simultaneous predicates and consumption groups.</summary>
    public static CompiledWorldInputPattern[] Compile(WorldInputCatalog catalog, WorldInputHistory policy, IReadOnlyList<WorldInputPattern> declarations) {
        if (declarations.Count > 64) { throw new ArgumentException(message: "inputPatterns exceeds 64 declared patterns"); }
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var groups = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var compiled = new CompiledWorldInputPattern[declarations.Count];

        for (var index = 0; (index < declarations.Count); index++) {
            var row = declarations[index];

            ArgumentException Invalid(string reason) => new(message: $"inputPatterns '{row.Name}': {reason}");
            if (string.IsNullOrWhiteSpace(value: row.Name) || !names.Add(item: row.Name) || (row.Steps.Count is < 1 or > 16) ||
                (row.MaximumDurationSeconds < 0) || (row.MaximumDurationSeconds > policy.RetentionSeconds) || (row.MaximumGapSeconds < 0)) {
                throw Invalid(reason: "expected a unique name, 1..16 steps, and durations within retained history");
            }
            var stages = new List<WorldInputPatternStage>();
            var seenGroups = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var step = 0; (step < row.Steps.Count);) {
                var first = row.Steps[step];
                var terms = new List<WorldInputPatternTerm>();

                if ((first.SimultaneousGroup is { } simultaneous) && !seenGroups.Add(item: simultaneous)) { throw Invalid(reason: "a simultaneous group must be contiguous"); }
                do {
                    var member = row.Steps[step++];

                    if (!catalog.TryGetLogicalId(member.Channel, out var channel)) { throw Invalid(reason: $"unknown channel '{member.Channel}'"); }
                    var shape = catalog.Channels[channel].Shape;

                    if (!Enum.IsDefined(value: member.Kind) || (member.Kind == WorldInputEventKind.Cancel) ||
                        ((member.Kind is WorldInputEventKind.Press or WorldInputEventKind.Release) && (shape != Protocol.ChannelShape.Binary)) ||
                        ((member.Kind == WorldInputEventKind.Text) && (shape != Protocol.ChannelShape.Text)) ||
                        ((member.Kind is WorldInputEventKind.AxisSample or WorldInputEventKind.AxisCrossing) && (shape is not (Protocol.ChannelShape.Bipolar or Protocol.ChannelShape.Unipolar)))) {
                        throw Invalid(reason: $"event kind differs from '{member.Channel}' shape");
                    }
                    var crossing = -1;

                    if (member.Kind == WorldInputEventKind.AxisCrossing) {
                        for (var c = 0; (c < (policy.Crossings?.Count ?? 0)); c++) {
                            if ((policy.Crossings![c].Name == member.Crossing) && (policy.Crossings[c].Channel == member.Channel)) { crossing = c; }
                        }
                        if (crossing < 0) { throw Invalid(reason: $"unknown crossing '{member.Crossing}'"); }
                    } else if (member.Crossing is not null) { throw Invalid(reason: "crossing names require AxisCrossing"); }
                    if ((member.ToleranceSeconds < 0) || (member.ToleranceSeconds > row.MaximumDurationSeconds) ||
                        (member.ToleranceSeconds != first.ToleranceSeconds) || (member.MinimumTickGap != first.MinimumTickGap) ||
                        (member.MaximumTickGap != first.MaximumTickGap) || (member.MaximumTickGap < member.MinimumTickGap)) {
                        throw Invalid(reason: "stage timing bounds are invalid or disagree inside a simultaneous group");
                    }
                    var term = new WorldInputPatternTerm(channel, member.Kind, member.RawValue, crossing);

                    if (terms.Any(predicate: other => ((other.Channel == term.Channel) && (other.Kind == term.Kind) && (other.Crossing == term.Crossing) &&
                        ((other.RawValue is null) || (term.RawValue is null) || (other.RawValue == term.RawValue))))) {
                        throw Invalid(reason: "simultaneous predicates must be disjoint; repeated events belong in ordered stages");
                    }
                    terms.Add(item: term);
                } while ((step < row.Steps.Count) && (first.SimultaneousGroup is not null) && (row.Steps[step].SimultaneousGroup == first.SimultaneousGroup));
                stages.Add(item: new(terms.ToArray(), Parse(value: first.ToleranceSeconds), first.MinimumTickGap, first.MaximumTickGap));
            }
            if ((((((long)stages.Count) * policy.EventCapacity) * row.Steps.Count) * sizeof(int)) > ((4 * 1024) * 1024)) { throw Invalid(reason: "matching scratch exceeds 4 MiB per owner"); }
            var held = (row.Held ?? []).Select(selector: name => {
                if (!catalog.TryGetLogicalId(id: out var id, name: name) || (catalog.Channels[id].Shape != Protocol.ChannelShape.Binary)) { throw Invalid(reason: $"held predicate '{name}' must name a binary action"); }
                return id;
            }).ToArray();
            var groupName = ((row.ConsumptionGroup is { } shared) ? ("shared:" + shared) : ("private:" + row.Name));

            if (!groups.TryGetValue(key: groupName, value: out var group)) { group = groups.Count; groups.Add(key: groupName, value: group); }
            compiled[index] = new() {
                Name = row.Name,
                Stages = stages.ToArray(),
                Duration = Parse(value: row.MaximumDurationSeconds),
                Gap = Parse(value: row.MaximumGapSeconds),
                BufferTicks = row.BufferTicks,
                Held = held,
                Group = group,
            };
        }
        return compiled;
    }

    private static FixedQ4816 Parse(decimal value) => FixedQ4816.Parse(value.ToString(provider: CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
/// <summary>The immutable endpoints of one selected event range.</summary>
public readonly record struct WorldInputMatch(ulong Epoch, ulong First, ulong Last, ulong FirstTick, ulong LastTick,
    FixedQ4816 FirstTime, FixedQ4816 LastTime);
