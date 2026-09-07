using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Previews a bounded local rearrangement of one deal template. Pinned children and external
    /// occupation and clearance volumes remain fixed. Existing positions are tried first, then nearby distribution offsets.
    /// The first feasible arrangement is returned; this does not claim a globally optimal packing.</summary>
    /// <param name="templateId">The template declaring a reflow policy.</param>
    /// <param name="principal">The actor who will submit the proposal.</param>
    /// <param name="proposal">Detached edits and price, on success.</param>
    /// <param name="reason">Named failure, without any mutation or payment.</param>
    /// <returns>Whether a layout and exact payment candidate was found within the authored work budget.
    /// Installation still validates current document and capacity constraints.</returns>
    public bool TryPreviewReflow(string templateId, WorldPrincipal principal, out WorldPlacementProposal? proposal, out string reason) {
        return TryPreviewReflow(new WorldPlacementReflowRequest(TemplateId: templateId), principal, out proposal, out reason);
    }

    /// <summary>Previews a bounded placement edit and neighbor rearrangement as one guarded batch.</summary>
    public bool TryPreviewReflow(WorldPlacementReflowRequest request, WorldPrincipal principal, out WorldPlacementProposal? proposal, out string reason) {
        if (request is null) { proposal = null; reason = "a reflow request is required"; return false; }
        if (!TryCaptureReflow(request, principal, out var definition, out var tick, out reason)) { proposal = null; return false; }
        var result = BuildReflowPreview(definition!, tick, request, principal);
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
        return TryStartReflowPreview(new WorldPlacementReflowRequest(TemplateId: templateId), principal, out pending, out reason);
    }

    /// <summary>Starts a background preview for a bounded placement request.</summary>
    public bool TryStartReflowPreview(WorldPlacementReflowRequest request, WorldPrincipal principal,
        out Task<(WorldPlacementProposal? Proposal, string Reason)>? pending, out string reason) {
        pending = null;
        if (request is null) { reason = "a reflow request is required"; return false; }
        if (!TryCaptureReflow(request, principal, out var definition, out var tick, out reason)) { return false; }
        try {
            pending = Task.Run(() => BuildReflowPreview(definition!, tick, request, principal));
            return true;
        } catch {
            // Task.Run can fail before the delegate owns the release (for example when the scheduler is
            // shutting down). The capture acquired the gate, so release it here and preserve the scheduler
            // exception for the caller instead of leaving every future preview permanently busy.
            Volatile.Write(ref m_reflowBusy, 0);
            throw;
        }
    }

    private bool TryCaptureReflow(WorldPlacementReflowRequest request, WorldPrincipal principal, out WorldDefinition? definition, out ulong tick, out string reason) {
        definition = null;
        tick = 0;
        reason = "another reflow preview is running";
        if (Interlocked.CompareExchange(ref m_reflowBusy, 1, 0) != 0) { return false; }
        try {
            var captured = ExecuteAuthorityOperation(() => {
                var template = ReflowTemplate(m_definition, request);
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
        } catch {
            // Capture runs before a worker exists, so its exception path owns the gate. Do not turn an
            // authority or admission failure into a successful-looking preview or strand the gate.
            Volatile.Write(ref m_reflowBusy, 0);
            throw;
        }
    }

    private (WorldPlacementProposal? Proposal, string Reason) BuildReflowPreview(WorldDefinition definition, ulong tick, WorldPlacementReflowRequest request, WorldPrincipal principal) {
        try {
            TryBuildReflowPreview(definition, tick, request, principal, out var proposal, out var reason);
            return (proposal, reason);
        } catch (OverflowException) {
            return (null, "reflow geometry exceeds the fixed-point range");
        } finally {
            Volatile.Write(ref m_reflowBusy, 0);
        }
    }

    private bool TryBuildReflowPreview(WorldDefinition definition, ulong tick, WorldPlacementReflowRequest request, WorldPrincipal principal, out WorldPlacementProposal? proposal, out string reason) {
        proposal = null;
        reason = string.Empty;
        var defaultSelection = request.PlacementIds is null;
        if (request.PlacementIds is { } requestedIds) {
            if (requestedIds.Count == 0 || requestedIds.Any(id => string.IsNullOrWhiteSpace(id)) ||
                requestedIds.Distinct(StringComparer.Ordinal).Count() != requestedIds.Count) {
                reason = "explicit reflow placement groups must contain distinct named members";
                return false;
            }
            var knownIds = definition.Placements.Select(placement => placement.Id).ToHashSet(StringComparer.Ordinal);
            if (requestedIds.Any(id => !knownIds.Contains(id))) {
                reason = "explicit reflow placement groups must name existing placements";
                return false;
            }
        }
        var template = ReflowTemplate(definition, request);
        if (template?.Deal is not { Reflow: { } policy }) {
            reason = "a reflow request requires a template declaring a reflow policy";
            return false;
        }
        if (defaultSelection && template.Deal.Preserve?.Transform != true) {
            reason = "dealt-child reflow requires instance-owned transforms";
            return false;
        }
        var children = (defaultSelection
            ? definition.Placements.Where(p => WorldPlacementDeal.IsChild(p, template))
            : definition.Placements.Where(p => request.PlacementIds!.Contains(p.Id, StringComparer.Ordinal)))
            .OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        if (children.Length == 0 || children.Length > 64 || children.Any(p => !HasReflowBounds(p) || p.Attach is not null || p.Inhabit is not null || p.Distribution is not null || p.Mirror is not null)) {
            reason = "reflow requires 1..64 static members with occupation or clearance volumes";
            return false;
        }
        if (children.Any(p => !string.Equals(p.Parent, template.Id, StringComparison.Ordinal))) {
            reason = "explicit reflow groups must share the template parent frame";
            return false;
        }
        var selectedIds = children.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (definition.Placements.Any(placement => placement.Parent is { } parent && selectedIds.Contains(parent))) {
            reason = "reflow members with child placements require a subtree-aware plan";
            return false;
        }
        var edits = request.Edits ?? [];
        if (edits.Any(edit => edit is null || !selectedIds.Contains(edit.PlacementId)) ||
            edits.Select(edit => edit.PlacementId).Distinct(StringComparer.Ordinal).Count() != edits.Count) {
            reason = "seed edits must name distinct members of the bounded placement group";
            return false;
        }
        var editById = edits.ToDictionary(edit => edit.PlacementId, StringComparer.Ordinal);
        var offsetCount = WorldPlacementDeal.InstanceCount(template, definition.Generation?.WorldSeed ?? 0UL);
        // Charge the bounded upper estimate before enumerating offsets, sorting, or compiling options.
        var setupWork = (long)children.Length * (offsetCount + 1L) * (1 + System.Numerics.BitOperations.Log2((uint)Math.Max(offsetCount, 1))) +
            (children.Sum(child => (long)(child.Spatial?.Count ?? 0)) + edits.Sum(edit => (long)(edit.Spatial?.Count ?? 0))) * (offsetCount + 1L);
        if (setupWork >= policy.CandidateBudget) { reason = "reflow exhausted its candidate budget preparing the search; no changes were made"; return false; }
        var offsets = WorldPlacementDeal.Offsets(template, definition.Generation?.WorldSeed ?? 0UL);
        var placementsById = definition.Placements.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var options = new WorldPlacement[children.Length][];
        for (var index = 0; index < children.Length; index++) {
            var child = children[index];
            if (editById.TryGetValue(child.Id, out var edit)) {
                if (IsPinned(child) && (edit.Position is not null || edit.YawDegrees is not null || edit.Scale is not null)) {
                    reason = $"pinned spatial member '{child.Id}' cannot receive a transform edit";
                    return false;
                }
                child = child with {
                    Position = edit.Position ?? child.Position,
                    YawDegrees = edit.YawDegrees ?? child.YawDegrees,
                    Scale = edit.Scale ?? child.Scale,
                    Spatial = edit.Spatial ?? child.Spatial
                };
            }
            if (!WorldDefinitionValidator.TryValidatePlacementGeometry(child, out reason)) { return false; }
            if (!HasReflowBounds(child)) {
                reason = $"spatial member '{child.Id}' must retain occupation or clearance volumes after edits";
                return false;
            }
            var origin = FixedVector3.FromVector3(child.Position);
            options[index] = (editById.ContainsKey(children[index].Id) || IsPinned(child)) ? [child] : [child, .. offsets
                .Where(offset => offset != origin)
                .OrderBy(offset => (offset - origin).LengthSquared)
                .Select(offset => child with { Position = offset.ToVector3() })];
        }
        var optionVolumes = options
            .Select(optionSet => optionSet.Select(option => WorldSpatialQueryCompilation.CompilePlacement(option, placementsById)).ToArray())
            .ToArray();
        if (optionVolumes.Any(optionSet => optionSet.Any(volumes =>
            !volumes.Any(volume => volume.Role is WorldPlacementSpatialRole.Occupation or WorldPlacementSpatialRole.Clearance)))) {
            reason = "reflow candidates require resolvable occupation or clearance volumes";
            return false;
        }
        // Query scope is known before blocker discovery. It encloses every original child and every candidate,
        // including an edited seed that moved or shrank, so discovery and the negative dependency share one region.
        var spatialRegion = WorldSpatialReadRegion.Empty;
        foreach (var volumes in optionVolumes.SelectMany(optionsForChild => optionsForChild)
            .Concat(children.Select(child => WorldSpatialQueryCompilation.CompilePlacement(child, placementsById)))) {
            foreach (var volume in volumes) {
                spatialRegion = spatialRegion.Enclose(volume.Bounds);
            }
        }
        var localRows = WorldDefinitionFingerprint.SpatialRows(definition, spatialRegion);
        var localVolumeCount = localRows.Sum(row => (long)(row.Spatial?.Count ?? 0));
        setupWork += IndexWork(localVolumeCount);
        if (setupWork >= policy.CandidateBudget) { reason = "reflow exhausted its candidate budget preparing local spatial reads; no changes were made"; return false; }
        var before = WorldSpatialQueryCompilation.Compile(localRows);
        if (before.Unsupported.Count > 0) {
            var unsupported = before.Unsupported[0];
            reason = $"spatial participant '{unsupported.PlacementId}' is unsupported for bounded reflow: {unsupported.Reason}";
            return false;
        }
        // Reuse compiled frames and shapes. Only the selected members are replaced by each candidate.
        var fixedVolumes = before.Volumes.Where(volume => !selectedIds.Contains(volume.PlacementId)).ToArray();
        setupWork += IndexWork(fixedVolumes.Length);
        if (setupWork >= policy.CandidateBudget) { reason = "reflow exhausted its candidate budget preparing the search; no changes were made"; return false; }
        var spatialIndex = new WorldSpatialQueryIndex(fixedVolumes);
        var selected = new WorldPlacement[children.Length];
        var selectedVolumes = new IReadOnlyList<CompiledSpatialVolume>[children.Length];
        var work = (int)setupWork;
        CoverageRead[] coverageInputs = [];
        if (request.PreserveInfluenceCoverage && !TryPrepareCoverage(before,
            optionVolumes.SelectMany(set => set).SelectMany(volumes => volumes), spatialRegion,
            ref work, policy.CandidateBudget, out coverageInputs)) {
            reason = "reflow exhausted its candidate budget preparing coverage reads";
            return false;
        }
        var exhausted = false;
        var coverageRejected = false;
        var coverageReason = string.Empty;
        bool Search(int index) {
            if (index == children.Length) {
                // Coverage is part of the leaf acceptance predicate. A collision-free arrangement that moves a
                // provider out of a selected target's authored coverage must backtrack to the next candidate.
                if (!request.PreserveInfluenceCoverage) { return true; }
                if (PreservesInfluenceCoverage(fixedVolumes, selectedVolumes, coverageInputs,
                    ref work, policy.CandidateBudget, out var coverageFailure, out var coverageBudgetExceeded)) { return true; }
                if (coverageBudgetExceeded) { exhausted = true; }
                coverageRejected = true;
                coverageReason = coverageFailure;
                return false;
            }
            for (var optionIndex = 0; optionIndex < options[index].Length; optionIndex++) {
                var option = options[index][optionIndex];
                if (++work > policy.CandidateBudget) { exhausted = true; return false; }
                var volumes = optionVolumes[index][optionIndex];
                var clear = true;
                foreach (var volume in volumes) {
                    var overlaps = spatialIndex.HasBlockingOverlap(volume, out var queryWork);
                    if (queryWork > policy.CandidateBudget - work) { exhausted = true; return false; }
                    work += queryWork;
                    if (overlaps) { clear = false; break; }
                }
                for (var previous = 0; clear && previous < index; previous++) {
                    foreach (var left in volumes) {
                        foreach (var right in selectedVolumes[previous]) {
                            if (++work > policy.CandidateBudget) { exhausted = true; return false; }
                            if (WorldSpatialQueryIndex.Conflicts(left, right)) { clear = false; break; }
                        }
                        if (!clear) { break; }
                    }
                }
                if (!clear) { continue; }
                selected[index] = option;
                selectedVolumes[index] = volumes;
                if (Search(index + 1)) { return true; }
                if (exhausted) { return false; }
            }
            return false;
        }
        if (!Search(0)) {
            reason = exhausted ? "reflow exhausted its candidate budget; no changes were made" :
                coverageRejected ? coverageReason : "no arrangement satisfies the declared occupation and clearance volumes at the available offsets";
            return false;
        }
        var mutations = new List<WorldMutation>();
        for (var index = 0; index < children.Length; index++) {
            if (children[index] != selected[index]) { mutations.Add(new WorldMutation.UpsertPlacement(principal, selected[index])); }
        }
        var moved = mutations.Count;
        if (moved == 0) { reason = "the current layout already satisfies its occupation and clearance contracts"; return false; }
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
        // The guard region remains local: distant placements do not tax this proposal, while the census' negative
        // space catches a new, moved, or enlarged obstacle entering it at commit.
        var spatialRead = new WorldSpatialReadDependency(
            spatialRegion.MinXRaw,
            spatialRegion.MinZRaw,
            spatialRegion.MaxXRaw,
            spatialRegion.MaxZRaw,
            WorldDefinitionFingerprint.ComputeSpatial(definition, spatialRegion),
            spatialRegion.MinYRaw,
            spatialRegion.MaxYRaw);
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in localRows.Append(template)) {
            WorldStateDocumentValues.CollectReferencedRows(placement, dependencies);
            if (placement.Deal is not { } deal) { continue; }
            dependencies.Add(deal.Row);
            if (deal.Variants is { } variants) { dependencies.Add(variants.Row); }
        }
        var stateRows = dependencies.Order(StringComparer.Ordinal).ToArray();
        var placementIds = localRows.Append(template)
            .Select(placement => placement.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedInputs = new WorldDefinitionReadDependency(
            placementIds,
            stateRows,
            WorldDefinitionFingerprint.ComputeInputs(definition, placementIds, stateRows));
        // Explicit input rows protect source membership, policy, bound participants, and referenced state. The
        // bounded spatial read separately catches a new, moved, or enlarged obstacle entering this local region.
        var batch = new WorldMutation.Batch(principal, mutations.ToArray(),
            ExpectedInputs: expectedInputs,
            ExpectedCells: expectedCells,
            ExpectedSpatialReads: [spatialRead]);
        var admission = ExecuteAuthorityOperation(() => TryAdmitCompleteMutation(batch, preMetered: true, out var verdict) ? string.Empty : verdict.Describe());
        if (admission.Length != 0) { reason = admission; return false; }
        // Pure preflight checks exact payment, including trait clamps. Installation owns full document, render
        // envelope and authority validation against the then-current world; none runs on this worker's snapshot.
        if (!TryCompose(definition, batch, tick, InstanceIdentity, out _, out reason, out _, patterns: null)) { return false; }
        proposal = new WorldPlacementProposal(batch, work, moved, cost) {
            AffectedIds = [.. children.Select(child => child.Id)],
            Constraints = [$"members<=64", $"candidates<={policy.CandidateBudget}", request.PreserveInfluenceCoverage ? "coverage cannot lose providers" : "coverage may change"]
        };
        return true;
    }

    private static bool HasReflowBounds(WorldPlacement placement) =>
        placement.Spatial?.Any(volume => volume?.Role is WorldPlacementSpatialRole.Occupation or WorldPlacementSpatialRole.Clearance) == true;

    private static bool IsPinned(WorldPlacement placement) =>
        placement.Spatial?.Any(volume => volume?.Pinned == true) == true;

    private static WorldPlacement? ReflowTemplate(WorldDefinition definition, WorldPlacementReflowRequest request) {
        var templateId = request.TemplateId ?? (request.PlacementIds is { Count: > 0 }
            ? definition.Placements.FirstOrDefault(placement => request.PlacementIds.Contains(placement.Id, StringComparer.Ordinal))?.Parent
            : null);
        return templateId is null ? null : WorldDefinitionRows.FindPlacement(id: templateId, placements: definition.Placements);
    }

    // The BVH's recursive sort has n log²(n) construction work. Charge its conservative estimate up front.
    private static long IndexWork(long count) {
        var levels = 1 + System.Numerics.BitOperations.Log2((ulong)Math.Max(count, 1));
        return count * levels * levels;
    }

    private readonly record struct CoverageRead(string PlacementId, string Channel, int Minimum);

    private static bool TryPrepareCoverage(WorldSpatialQueryIndex before, IEnumerable<CompiledSpatialVolume> candidates,
        WorldSpatialReadRegion region, ref int work, int budget, out CoverageRead[] reads) {
        reads = [];
        var channels = before.Volumes.Concat(candidates)
            .Where(volume => volume.Role == WorldPlacementSpatialRole.Influence)
            .Select(volume => volume.Channel!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var targets = before.Volumes.Where(volume => volume.Role == WorldPlacementSpatialRole.Occupation)
            .DistinctBy(volume => volume.PlacementId, StringComparer.Ordinal)
            .Where(volume => region.Bounds.Intersects(new FixedSpatialAabb(volume.Shape.Center, FixedVector3.Zero)))
            .Select(volume => volume.PlacementId).Order(StringComparer.Ordinal).ToArray();
        var cost = (long)targets.Length * channels.Length * Math.Max(before.Volumes.Count, 1);
        if (cost > budget - work) { return false; }
        work += (int)cost;
        reads = [.. targets.SelectMany(target => channels.Select(channel =>
            new CoverageRead(target, channel, before.CountInfluences(channel, target))))];
        return true;
    }

    private static bool PreservesInfluenceCoverage(IReadOnlyList<CompiledSpatialVolume> fixedVolumes,
        IReadOnlyList<IReadOnlyList<CompiledSpatialVolume>> selectedVolumes,
        IReadOnlyList<CoverageRead> reads,
        ref int work, int budget, out string reason, out bool budgetExceeded) {
        reason = string.Empty;
        budgetExceeded = false;
        if (reads.Count == 0) { return true; }
        var volumeCount = fixedVolumes.Count + selectedVolumes.Sum(volumes => (long)volumes.Count);
        var cost = IndexWork(volumeCount) + reads.Count * volumeCount;
        if (cost > budget - work) {
            budgetExceeded = true;
            reason = "reflow exhausted its candidate budget while proving influence coverage";
            return false;
        }
        work += (int)cost;
        var after = new WorldSpatialQueryIndex([.. fixedVolumes, .. selectedVolumes.SelectMany(volumes => volumes)]);
        foreach (var read in reads) {
            if (after.CountInfluences(read.Channel, read.PlacementId) < read.Minimum) {
                reason = $"reflow would reduce influence coverage for '{read.PlacementId}' on channel '{read.Channel}'";
                return false;
            }
        }
        return true;
    }
}
