namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // The document's root members, by JSON name, in the serializer's order.
    private static readonly IReadOnlyList<string> RootMembers = WorldDocumentWalker.MemberNames(owner: typeof(WorldDefinition));

    /// <summary>Returns what installing a reload's candidate over the live document rebuilds, decided by which of the
    /// document's root members the candidate holds as another instance than the live document: nothing, when it holds
    /// every member as the live document's own; a value install, when only state rows' values moved; a presentation
    /// install, when only creations changed, each in what shading reads, and shade programs; a full install
    /// otherwise.</summary>
    /// <remarks>A reload's candidate holds the live document's own instance of every member and keyed row its change
    /// leaves equal (<see cref="Protocol.WorldReloadDelta.TryMergeOver"/>), so an instance that differs is a change. The
    /// value and presentation installs are named by the members they may change, and every other member's change takes
    /// the full install, so a product the narrower installs keep never stands stale for a member they did not
    /// list.</remarks>
    /// <param name="candidate">The reload's candidate.</param>
    /// <param name="live">The live document it was merged over.</param>
    /// <param name="rows">The state rows a value install loads; empty for every other kind.</param>
    /// <returns>The install kind.</returns>
    internal static WorldInstallKind ReloadInstallKindOf(WorldDefinition candidate, WorldDefinition live, out IReadOnlyList<string> rows) {
        rows = [];

        var changed = new List<string>();

        foreach (var name in RootMembers) {
            var before = WorldDocumentWalker.ReadMember(name: name, root: live);
            var after = WorldDocumentWalker.ReadMember(name: name, root: candidate);

            if (!ReferenceEquals(objA: before, objB: after) && !((before is string or ValueType) && before.Equals(obj: after))) { changed.Add(item: name); }
        }
        if (changed.Count == 0) { return WorldInstallKind.Nothing; }
        if (changed is [WorldDefinitionMembers.StateMemberName] && TryNameValueRows(candidate: candidate, live: live, rows: out var valueRows)) {
            rows = valueRows;
            return WorldInstallKind.Value;
        }
        return (changed.TrueForAll(match: static name => name is PrototypesMemberName or WorldDocumentBasis.ShadesMemberName) && ChangesShadingAlone(candidate: candidate, live: live))
            ? WorldInstallKind.Presentation
            : WorldInstallKind.Full;
    }

    private const string PrototypesMemberName = "prototypes";

    // Whether every creation row the candidate holds as another instance is the same creation differing only in what
    // shading reads, in the same order.
    private static bool ChangesShadingAlone(WorldDefinition candidate, WorldDefinition live) {
        var after = candidate.Creations;
        var before = live.Creations;

        if (after.Count != before.Count) { return false; }
        for (var index = 0; (index < after.Count); index++) {
            if (!ReferenceEquals(objA: after[index], objB: before[index]) &&
                (!string.Equals(a: after[index].Id.Value, b: before[index].Id.Value, comparisonType: StringComparison.Ordinal) || !before[index].DiffersInShadingAlone(other: after[index]))) {
                return false;
            }
        }
        return true;
    }
    // The state rows whose values alone moved, when nothing else of the state section did: the same parts beside the
    // rows, no pools, the same compiled catalog, and every row either the live one or the same row with new values.
    private static bool TryNameValueRows(WorldDefinition candidate, WorldDefinition live, out IReadOnlyList<string> rows) {
        rows = [];

        var after = candidate.StateRaw;
        var before = live.StateRaw;

        if ((after is null) || (before is null) ||
            (after.Pools is { Count: > 0 }) || (after.PairPools is { Count: > 0 }) || (before.Pools is { Count: > 0 }) || (before.PairPools is { Count: > 0 }) ||
            !SameStateParts(left: after, right: before) || !ReferenceEquals(objA: candidate.StateCatalog, objB: live.StateCatalog)) {
            return false;
        }

        var afterRows = (after.World ?? []);
        var beforeRows = (before.World ?? []);

        if (afterRows.Count != beforeRows.Count) { return false; }

        var named = new List<string>();

        for (var index = 0; (index < afterRows.Count); index++) {
            if (ReferenceEquals(objA: afterRows[index], objB: beforeRows[index])) { continue; }
            // A row a private stream draws is drawn again by the full install, under the authority's own sampler.
            if (!string.Equals(a: afterRows[index].Name.Value, b: beforeRows[index].Name.Value, comparisonType: StringComparison.Ordinal) ||
                (afterRows[index].Draw?.PrivateStream == true) || !DiffersInValuesAlone(before: beforeRows[index], row: afterRows[index])) {
                return false;
            }
            named.Add(item: afterRows[index].Name.Value);
        }
        if (named.Count == 0) { return false; }
        rows = named;
        return true;
    }
}
