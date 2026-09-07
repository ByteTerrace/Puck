using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A detached layout preview. Its ordinary guarded batch is the only commit artifact.</summary>
/// <param name="Mutation">Placement changes and payment, with a fingerprint of their base document.</param>
/// <param name="Candidates">Candidate and overlap checks performed.</param>
/// <param name="Moved">Number of changed placement transforms.</param>
/// <param name="Cost">Authored Int payment.</param>
public sealed record WorldPlacementProposal(WorldMutation.Batch Mutation, int Candidates, int Moved, long Cost);

public sealed partial class WorldServer {
    /// <summary>Previews a bounded local rearrangement of one deal template. Pinned children and external
    /// footprints remain fixed. Existing positions are tried first, then nearby distribution offsets.
    /// The first feasible arrangement is returned; this does not claim a globally optimal packing.</summary>
    /// <param name="templateId">The template declaring a reflow policy.</param>
    /// <param name="principal">The actor who will submit the proposal.</param>
    /// <param name="proposal">Detached edits and price, on success.</param>
    /// <param name="reason">Named failure, without any mutation or payment.</param>
    /// <returns>Whether a layout and exact payment candidate was found within the authored work budget.
    /// Installation still validates current document and capacity constraints.</returns>
    public bool TryPreviewReflow(string templateId, WorldPrincipal principal, out WorldPlacementProposal? proposal, out string reason) {
        if (!TryCaptureReflow(templateId, principal, out var definition, out var tick, out reason)) { proposal = null; return false; }
        var result = BuildReflowPreview(definition!, tick, templateId, principal);
        proposal = result.Proposal;
        reason = result.Reason;
        return proposal is not null;
    }

    private int m_reflowBusy;

    /// <summary>Starts one bounded background preview per server. The immutable document is captured under a short
    /// authority lock; search and composition run outside it. Busy requests do not enqueue more work.</summary>
    /// <param name="templateId">The template declaring reflow.</param>
    /// <param name="principal">The requesting actor, metered through normal mutation admission.</param>
    /// <param name="pending">The detached proposal or named refusal, when work completes.</param>
    /// <param name="reason">An immediate admission or busy refusal.</param>
    /// <returns>Whether a worker was started.</returns>
    public bool TryStartReflowPreview(string templateId, WorldPrincipal principal,
        out Task<(WorldPlacementProposal? Proposal, string Reason)>? pending, out string reason) {
        pending = null;
        if (!TryCaptureReflow(templateId, principal, out var definition, out var tick, out reason)) { return false; }
        pending = Task.Run(() => BuildReflowPreview(definition!, tick, templateId, principal));
        return true;
    }

    private bool TryCaptureReflow(string templateId, WorldPrincipal principal, out WorldDefinition? definition, out ulong tick, out string reason) {
        definition = null;
        tick = 0;
        reason = "another reflow preview is running";
        if (Interlocked.CompareExchange(ref m_reflowBusy, 1, 0) != 0) { return false; }
        var captured = ExecuteAuthorityOperation(() => {
            var template = WorldDefinitionRows.FindPlacement(id: templateId, placements: m_definition.Placements);
            if (template?.Deal?.Reflow is null) { return (Definition: (WorldDefinition?)null, Tick: 0UL, Reason: "the template declares no reflow policy"); }
            if (!TryAdmitCompleteMutation(new WorldMutation.UpsertPlacement(principal, template), preMetered: false, out var admission)) {
                return (Definition: (WorldDefinition?)null, Tick: 0UL, Reason: admission.Describe());
            }
            return (Definition: (WorldDefinition?)m_definition, Tick: m_lastCompletedTick, Reason: string.Empty);
        });
        definition = captured.Definition;
        tick = captured.Tick;
        reason = captured.Reason;
        if (definition is not null) { return true; }
        Volatile.Write(ref m_reflowBusy, 0);
        return false;
    }

    private (WorldPlacementProposal? Proposal, string Reason) BuildReflowPreview(WorldDefinition definition, ulong tick, string templateId, WorldPrincipal principal) {
        try {
            TryBuildReflowPreview(definition, tick, templateId, principal, out var proposal, out var reason);
            return (proposal, reason);
        } catch (OverflowException) {
            return (null, "reflow geometry exceeds the fixed-point range");
        } finally {
            Volatile.Write(ref m_reflowBusy, 0);
        }
    }

    private bool TryBuildReflowPreview(WorldDefinition definition, ulong tick, string templateId, WorldPrincipal principal, out WorldPlacementProposal? proposal, out string reason) {
        proposal = null;
        reason = string.Empty;
        var template = WorldDefinitionRows.FindPlacement(id: templateId, placements: definition.Placements);
        if (template?.Deal is not { Reflow: { } policy, Preserve.Transform: true }) {
            reason = $"placement '{templateId}' declares no reflow policy with instance-owned transforms";
            return false;
        }
        var children = definition.Placements.Where(p => WorldPlacementDeal.IsChild(p, template)).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        if (children.Length == 0 || children.Length > 64 || children.Any(p => p.Footprint is null || p.Attach is not null || p.Inhabit is not null || p.Distribution is not null)) {
            reason = "reflow requires 1..64 static children with explicit footprints";
            return false;
        }
        var blockerRows = definition.Placements.Where(p => p.Footprint is not null && p.Deal is null && !WorldPlacementDeal.IsChild(p, template)).ToArray();
        foreach (var placement in children.Concat(blockerRows)) {
            var frame = placement;
            while (true) {
                if (frame.Attach is not null || frame.Inhabit is not null || frame.Mirror is not null ||
                    (frame.Distribution is not null && frame.Deal is null) || (frame != placement && frame.Scale != 1f)) {
                    reason = $"footprint '{placement.Id}' requires a static single frame with unit-scale ancestors";
                    return false;
                }
                if (frame.Parent is not { } parent) { break; }
                frame = WorldDefinitionRows.FindPlacement(id: parent, placements: definition.Placements)!;
            }
        }
        var offsetCount = WorldPlacementDeal.InstanceCount(template, definition.Generation?.WorldSeed ?? 0UL);
        var setupWork = (long)children.Length * (offsetCount + 1L) * (1 + System.Numerics.BitOperations.Log2((uint)Math.Max(offsetCount, 1))) + blockerRows.Length;
        if (setupWork >= policy.CandidateBudget) { reason = "reflow exhausted its candidate budget preparing the search; no changes were made"; return false; }
        var offsets = WorldPlacementDeal.Offsets(template, definition.Generation?.WorldSeed ?? 0UL);
        var blockers = blockerRows.Select(p => ReflowBox.Of(definition, p)).ToArray();
        var options = new WorldPlacement[children.Length][];
        for (var index = 0; index < children.Length; index++) {
            var child = children[index];
            var origin = FixedVector3.FromVector3(child.Position);
            options[index] = child.Footprint!.Pinned ? [child] : [child, .. offsets
                .Where(offset => offset != origin)
                .OrderBy(offset => (offset - origin).LengthSquared)
                .Select(offset => child with { Position = offset.ToVector3() })];
        }
        var selected = new WorldPlacement[children.Length];
        var boxes = new ReflowBox[children.Length];
        var work = (int)setupWork;
        var exhausted = false;
        bool Search(int index) {
            if (index == children.Length) { return true; }
            foreach (var option in options[index]) {
                if (++work > policy.CandidateBudget) { exhausted = true; return false; }
                var box = ReflowBox.Of(definition, option);
                var clear = true;
                foreach (var blocker in blockers) {
                    if (++work > policy.CandidateBudget) { exhausted = true; return false; }
                    if (box.Overlaps(blocker)) { clear = false; break; }
                }
                for (var previous = 0; clear && previous < index; previous++) {
                    if (++work > policy.CandidateBudget) { exhausted = true; return false; }
                    clear = !box.Overlaps(boxes[previous]);
                }
                if (!clear) { continue; }
                selected[index] = option;
                boxes[index] = box;
                if (Search(index + 1)) { return true; }
                if (exhausted) { return false; }
            }
            return false;
        }
        if (!Search(0)) {
            reason = exhausted ? "reflow exhausted its candidate budget; no changes were made" : "no arrangement satisfies the declared footprints at the available offsets";
            return false;
        }
        var mutations = new List<WorldMutation>();
        for (var index = 0; index < children.Length; index++) {
            if (children[index] != selected[index]) { mutations.Add(new WorldMutation.UpsertPlacement(principal, selected[index])); }
        }
        var moved = mutations.Count;
        if (moved == 0) { reason = "the current layout already satisfies its footprint contracts"; return false; }
        long cost;
        WorldStateExpectation[]? expectedCells = null;
        try { cost = checked(policy.CostPerMove * moved); }
        catch (OverflowException) { reason = "reflow price exceeds the Int range"; return false; }
        if (cost > 0) {
            if (policy.CostRow is { } payerRow && principal != WorldPrincipal.World &&
                !ExecuteAuthorityOperation(() => (bool)m_grants.Allows(principal, WorldCapability.Observe, GrantSubject.State(payerRow)))) {
                reason = "the payer state row is not observable by this principal";
                return false;
            }
            if (policy.CostRow is not { } row || !WorldStateReader.TryRead(definition, row, policy.CostKey, tick, out var payer, out var value, out _) ||
                payer.Kind != CellKind.Int || value is not { } available || available < cost) {
                reason = "the authored payer cannot cover the reflow price";
                return false;
            }
            mutations.Add(new WorldMutation.UpsertStateCell(principal, row, policy.CostKey ?? WorldStateRow.SlotKey.Value, -cost, WorldDocumentWriteKind.Add));
            expectedCells = [new WorldStateExpectation(row, policy.CostKey, cost, ActionStateComparison.GreaterOrEqual, -cost, CellKind.Int)];
        }
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in WorldDefinitionFingerprint.LayoutRows(definition, templateId)) {
            WorldStateDocumentValues.CollectReferencedRows(placement, dependencies);
            if (placement.Deal is not { } deal) { continue; }
            dependencies.Add(deal.Row);
            if (deal.Variants is { } variants) { dependencies.Add(variants.Row); }
        }
        var stateRows = dependencies.Order(StringComparer.Ordinal).ToArray();
        var batch = new WorldMutation.Batch(principal, mutations.ToArray(), WorldDefinitionFingerprint.Compute(definition, stateRows, templateId), expectedCells, stateRows, templateId);
        var admission = ExecuteAuthorityOperation(() => TryAdmitCompleteMutation(batch, preMetered: true, out var verdict) ? string.Empty : verdict.Describe());
        if (admission.Length != 0) { reason = admission; return false; }
        // Pure preflight checks exact payment, including trait clamps. Installation owns full document, render
        // envelope and authority validation against the then-current world; none runs on this worker's snapshot.
        if (!TryCompose(definition, batch, tick, InstanceIdentity, out _, out reason, out _, patterns: null)) { return false; }
        proposal = new WorldPlacementProposal(batch, work, moved, cost);
        return true;
    }

    // Conservative world-axis envelopes of the authored rectangles. Rotation never shrinks clearance;
    // an eight-raw-unit margin covers fixed rotation/extent rounding. Overlap is planar by contract.
    private readonly record struct ReflowBox(FixedQ4816 X, FixedQ4816 Z, FixedQ4816 HalfX, FixedQ4816 HalfZ) {
        public bool Overlaps(ReflowBox other) => FixedQ4816.Abs(X - other.X) < HalfX + other.HalfX && FixedQ4816.Abs(Z - other.Z) < HalfZ + other.HalfZ;
        public static ReflowBox Of(WorldDefinition definition, WorldPlacement placement) {
            var parent = placement.Parent is { } id ? definition.PlacementFrames[id] : default;
            var position = parent.Position + WorldPlacementFrameCompilation.RotateY(placement.Position, parent.YawDegrees);
            var yaw = FixedQ4816.FromDouble((parent.YawDegrees + placement.YawDegrees) * (Math.PI / 180));
            var (sin, cos) = FixedQ4816.SinCos(yaw);
            var footprint = placement.Footprint!;
            var scale = FixedQ4816.FromDouble(placement.Scale);
            var x = FixedQ4816.FromDouble(footprint.HalfWidth) * scale;
            var z = FixedQ4816.FromDouble(footprint.HalfDepth) * scale;
            var margin = FixedQ4816.FromDouble(footprint.Clearance) + FixedQ4816.FromRawBits(8);
            return new(FixedQ4816.FromDouble(position.X), FixedQ4816.FromDouble(position.Z),
                FixedQ4816.Abs(cos) * x + FixedQ4816.Abs(sin) * z + margin,
                FixedQ4816.Abs(sin) * x + FixedQ4816.Abs(cos) * z + margin);
        }
    }
}
