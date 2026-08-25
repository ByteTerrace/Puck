using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Describes every placement carrying a response trait: its current prototype, and which authored
    /// condition (if any) currently holds at its coupled cell.</summary>
    public string DescribeResponses() {
        if (m_population.Fields is not { } lattice) {
            return "[world.responses: none — no fields section]";
        }

        var lines = new List<string>();

        foreach (var placement in m_definition.Placements) {
            if (placement.Respond is not { Count: > 0 } responses) {
                continue;
            }

            var matchedIndex = ResolveMatchingResponse(
                lattice: lattice,
                placement: placement,
                responses: responses,
                tick: m_lastCompletedTick
            );

            lines.Add(item: ((matchedIndex >= 0)
                ? $"'{placement.Id}' prototype={placement.PrototypeId} holds=[{matchedIndex}] {responses[matchedIndex].When.Field} {responses[matchedIndex].When.Comparison} -> {responses[matchedIndex].PrototypeId}"
                : $"'{placement.Id}' prototype={placement.PrototypeId} holds=none"
            ));
        }

        return ((lines.Count == 0)
            ? "[world.responses: none declared]"
            : $"[world.responses: {string.Join(separator: "; ", values: lines)}]"
        );
    }
    // Runs immediately after StepFields (WorldServer.Step.cs), so a response condition reads THIS tick's own lattice
    // writes rather than a tick-stale value — a burning tree's stump swap fires the same tick the char field crosses
    // its threshold.
    private void SweepPlacementResponses(ulong tick) {
        if (m_population.Fields is not { } lattice) {
            return;
        }

        foreach (var placement in m_definition.Placements) {
            if (placement.Respond is not { Count: > 0 } responses) {
                continue;
            }

            var matchedIndex = ResolveMatchingResponse(
                lattice: lattice,
                placement: placement,
                responses: responses,
                tick: tick
            );

            if (matchedIndex < 0) {
                continue;
            }

            var target = responses[matchedIndex].PrototypeId;

            if (string.Equals(
                a: placement.PrototypeId,
                b: target,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            var previous = placement.PrototypeId;

            // WorldPrincipal.World — the same structural-exemption door StampContribution/RetractContribution use —
            // so the swap is journalled and undoable through the ordinary UpsertPlacement compose arm.
            if (!TryApplyMutation(
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0,
                mutation: new WorldMutation.UpsertPlacement(
                    Placement: (placement with { PrototypeId = target }),
                    Principal: WorldPrincipal.World
                ),
                preMetered: false,
                tick: tick
            )) {
                continue;
            }

            Console.Error.WriteLine(value: $"[world.respond: '{placement.Id}' {previous} -> {target} (entry {matchedIndex}: {responses[matchedIndex].When.Field} {responses[matchedIndex].When.Comparison})]");
        }
    }
    // The first authored entry whose condition holds at the placement's coupled cell, or -1 when none do (or the
    // placement's authored, static position never couples to the lattice at all).
    private int ResolveMatchingResponse(WorldFieldLattice lattice, WorldPlacement placement, IReadOnlyList<WorldPlacementResponse> responses, ulong tick) {
        if (!lattice.TryBodyCellOf(
            position: FixedVector3.FromVector3(value: placement.Position),
            cell: out var cell
        )) {
            return -1;
        }

        for (var index = 0; (index < responses.Count); index++) {
            var condition = responses[index].When;

            if (
                !lattice.TryFieldIndex(name: condition.Field, field: out var field) ||
                !lattice.Holds(
                field: field,
                cell: cell,
                comparison: condition.Comparison,
                expected: condition.Value,
                readScalar: row => ReadScalarSlot(
                    row: row,
                    tick: tick
                )
            )
            ) {
                continue;
            }

            return index;
        }

        return -1;
    }
}
