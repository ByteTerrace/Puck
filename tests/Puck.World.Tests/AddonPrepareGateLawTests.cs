using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// The addon-lifecycle fold's core transaction, proved against <see cref="WorldServer"/>'s apply pipeline through a
/// scripted <see cref="IWorldAddonHost"/> double rather than a real Wasmtime guest (this project references neither
/// <c>Puck.World.Addons</c> nor <c>Puck.Scripting</c>): an <c>UpsertAddon</c> mutation's addon-prepare gate refuses
/// the WHOLE mutation — document byte-identical, no plan committed — when the attached host's
/// <see cref="IWorldAddonHost.TryPrepare"/> refuses, and applies normally when it accepts. Only <c>UpsertAddon</c>/
/// <c>RemoveAddon</c> ever reach the gate at all — a mutation on any other section never calls
/// <see cref="IWorldAddonHost.TryPrepare"/>, proved by the plan-count assertions below staying at their pre-call
/// values across an unrelated section.
/// </summary>
public sealed class AddonPrepareGateLawTests {
    [Fact]
    public void UnpreparableAddonRowRefusesTheWholeMutation_PreparableRowApplies() {
        using var fixture = Fixtures.FreshServer();
        var host = new RecordingAddonHost();

        fixture.Server.AttachAddons(runtime: host);

        Laws.RefusalWithControl(
            lawId: "addon.prepare-gate-all-or-nothing",
            deniedOutcome: () => {
                host.RefuseWhen = static candidate => HasAddonNamed(
                    definition: candidate,
                    name: "boom"
                );

                return ApplyAndObserveChange(
                    fixture: fixture,
                    name: "boom"
                );
            },
            controlOutcome: () => {
                host.RefuseWhen = null;

                return ApplyAndObserveChange(
                    fixture: fixture,
                    name: "ok"
                );
            }
        );
    }
    [Fact]
    public void PlanCommitsOnceOnAcceptance_NeverCreatedOnRefusal_NeverDisposedAfterCommit() {
        using var fixture = Fixtures.FreshServer();
        var host = new RecordingAddonHost {
            RefuseWhen = static _ => true,
        };

        fixture.Server.AttachAddons(runtime: host);
        Submit(
            fixture: fixture,
            name: "refused"
        );

        Assert.Empty(collection: host.LivePlans);
        Assert.Equal(
            expected: 0,
            actual: host.CommitCallCount
        );

        host.RefuseWhen = null;
        Submit(
            fixture: fixture,
            name: "accepted"
        );

        var plan = Assert.Single(collection: host.LivePlans);

        Assert.Equal(
            expected: 1,
            actual: host.CommitCallCount
        );
        Assert.Equal(
            expected: 1,
            actual: host.FinishCallCount
        );
        Assert.True(condition: plan.Committed, userMessage: "the accepted mutation's plan was never committed");
        Assert.False(condition: plan.Disposed, userMessage: "a committed plan must never be disposed — WorldServer's own linear-ownership floor only disposes an UNcommitted plan");
        Assert.True(condition: plan.Finished, userMessage: "the committed plan's deferred narration/retire step (Finish) never ran");
    }
    [Fact]
    public void NoAddonHostAttachedRefusesAnAddonAffectingMutation_UnrelatedMutationStillApplies() {
        using var fixture = Fixtures.FreshServer();

        // No AttachAddons call at all — m_addons stays null, exactly like a server built but never wired to a host.
        Laws.RefusalWithControl(
            lawId: "addon.no-host-refuses",
            deniedOutcome: () => {
                var before = fixture.DefinitionBytes();

                fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertAddon(
                    Addon: new WorldAddonRow(Name: "no-host", ModulePath: "unreachable.wasm", Hash: "sha256-64/0000000000000000", Fuel: 1000UL, Enabled: true),
                    Principal: WorldPrincipal.Console
                ));
                fixture.Step();

                var after = fixture.DefinitionBytes();

                // A hostless server must refuse the addon-affecting mutation by name, never silently accept it
                // with no effect — "did it change" must read false.
                return !before.AsSpan().SequenceEqual(other: after);
            },
            controlOutcome: () => {
                var before = fixture.DefinitionBytes();

                fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
                    Principal: WorldPrincipal.Console,
                    Row: new WorldStateRow(Name: WorldCellName.Parse(candidate: "no-host-control-probe"), Kind: CellKind.Int)
                ));
                fixture.Step();

                var after = fixture.DefinitionBytes();

                return !before.AsSpan().SequenceEqual(other: after);
            }
        );
    }
    [Fact]
    public void UndoDisposesEveryIntermediateProbe_CommitsOnlyTheFinalReconcilePlan() {
        using var fixture = Fixtures.FreshServer();
        var host = new RecordingAddonHost();

        fixture.Server.AttachAddons(runtime: host);
        Submit(
            fixture: fixture,
            name: "a"
        );
        Submit(
            fixture: fixture,
            name: "b"
        );
        host.Reset();

        fixture.Server.EnqueueUndo(
            count: 1,
            principal: WorldPrincipal.Console
        );
        fixture.Step();

        // ApplyUndo re-plays the ONE kept journal entry (UpsertAddon 'a') as a throwaway probe — proved fallible,
        // never committed, disposed immediately — then runs ONE final current-to-candidate reconcile that actually
        // commits.
        Assert.Equal(
            expected: 2,
            actual: host.LivePlans.Count
        );

        var probe = host.LivePlans[0];
        var final = host.LivePlans[1];

        Assert.True(condition: probe.Disposed, userMessage: "the intermediate undo probe was never disposed");
        Assert.False(condition: probe.Committed, userMessage: "the intermediate undo probe must never commit");
        Assert.False(condition: final.Disposed, userMessage: "the final undo reconcile plan must not be disposed after it commits");
        Assert.True(condition: final.Committed, userMessage: "the final undo reconcile plan was never committed");
    }
    [Fact]
    public void UnrelatedSectionMutationNeverTouchesThePrepareGate() {
        using var fixture = Fixtures.FreshServer();
        var host = new RecordingAddonHost {
            RefuseWhen = static _ => true, // would refuse EVERYTHING if ever consulted
        };

        fixture.Server.AttachAddons(runtime: host);

        var before = fixture.DefinitionBytes();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: new WorldStateRow(Name: WorldCellName.Parse(candidate: "addon-gate-probe"), Kind: CellKind.Int)
        ));
        fixture.Step();

        var after = fixture.DefinitionBytes();

        Assert.False(condition: before.AsSpan().SequenceEqual(other: after), userMessage: "the control mutation itself did not apply — the fixture is broken, not the law");
        Assert.Equal(
            expected: 0,
            actual: host.TryPrepareCallCount
        );
    }
    // Proves the WorldServer.Step half of stable completion routing: the instance token a caller submits on
    // EnqueueMutation travels unchanged to CompleteMutation, per pending op, even when an ordinary addon-affecting
    // mutation drains between two addon-sourced completions carrying different tokens. The other half — that
    // CompleteMutation resolves a token against a mounted guest's identity rather than its position, so a queued
    // removal/reorder can never deliver one guest's completion to another — lives inside
    // Puck.World.Addons.WorldAddonRuntime, which this project cannot reference.
    [Fact]
    public void AddonSourcedCompletionCarriesItsOwnSubmittedTokenAcrossAnInterveningAddonMutation() {
        using var fixture = Fixtures.FreshServer();
        var host = new RecordingAddonHost();

        fixture.Server.AttachAddons(runtime: host);

        fixture.Server.EnqueueMutation(
            mutation: new WorldMutation.UpsertStateRow(
                Principal: WorldPrincipal.Console,
                Row: new WorldStateRow(Name: WorldCellName.Parse(candidate: "completion-token-probe-a"), Kind: CellKind.Int)
            ),
            sourceAddonInstanceId: 111L,
            actOrdinal: 7
        );
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertAddon(
            Addon: new WorldAddonRow(Name: "intervening", ModulePath: "unreachable.wasm", Hash: "sha256-64/0000000000000000", Fuel: 1000UL, Enabled: true),
            Principal: WorldPrincipal.Console
        ));
        fixture.Server.EnqueueMutation(
            mutation: new WorldMutation.UpsertStateRow(
                Principal: WorldPrincipal.Console,
                Row: new WorldStateRow(Name: WorldCellName.Parse(candidate: "completion-token-probe-b"), Kind: CellKind.Int)
            ),
            sourceAddonInstanceId: 222L,
            actOrdinal: 9
        );
        fixture.Step();

        Assert.Equal(
            expected: [(111L, ((ushort)7), true), (222L, ((ushort)9), true)],
            actual: host.CompletedMutations
        );
    }
    // The plan-ownership guard now covers contention-array staging, not only Commit — see WorldServer.
    // MutationApply.cs's TryApplyMutation (and its ApplyRebuild/ApplyUndo siblings): the whole sequence from a
    // successful TryPrepare through Commit runs under ONE try/finally. A MountedCount this large forces
    // StageAddonContentionArrays' own capacity arithmetic (Population.LocalSeatCount + mountedCount*2) to wrap, in
    // unchecked int32 arithmetic, into a deeply negative array length, throwing OverflowException from INSIDE that
    // guarded region — proving the plan a successful TryPrepare returned is still disposed when something after it,
    // but before Commit, throws.
    [Fact]
    public void AddonPlanIsDisposedWhenContentionArrayStagingThrows() {
        using var fixture = Fixtures.FreshServer();
        var host = new RecordingAddonHost {
            NextPlanMountedCount = 1_500_000_000,
        };

        fixture.Server.AttachAddons(runtime: host);

        var thrown = Record.Exception(testCode: () => Submit(
            fixture: fixture,
            name: "overflow-probe"
        ));

        Assert.IsType<OverflowException>(@object: thrown);

        var plan = Assert.Single(collection: host.LivePlans);

        Assert.True(condition: plan.Disposed, userMessage: "an exception between a successful TryPrepare and Commit must still dispose the plan — the ownership guard now covers contention-array staging too");
        Assert.False(condition: plan.Committed, userMessage: "a plan that never reached Commit must not read as committed");
    }
    // ApplyRebuild's own null-host gate (finding 3): with no addon host attached at all, a candidate whose only
    // addon row is ENABLED must refuse — installing it would leave the document claiming a mounted guest no host
    // can ever run. A candidate whose only addon row is DISABLED stays vacuous, exactly like an addon-free one.
    [Fact]
    public void ApplyRebuildRefusesAnEnabledAddonRowWithNoHostAttached_AllDisabledCandidateInstalls() {
        using var fixture = Fixtures.FreshServer();

        // No AttachAddons call at all — m_addons stays null, exactly like a server built but never wired to a host.
        Laws.RefusalWithControl(
            lawId: "addon.rebuild-no-host-refuses-enabled-row",
            deniedOutcome: () => ApplyRebuildAndObserveChange(
                fixture: fixture,
                row: new WorldAddonRow(Name: "no-host-enabled", ModulePath: "unreachable.wasm", Hash: "sha256-64/0000000000000000", Fuel: 1000UL, Enabled: true)
            ),
            controlOutcome: () => ApplyRebuildAndObserveChange(
                fixture: fixture,
                row: new WorldAddonRow(Name: "no-host-disabled", ModulePath: "unreachable.wasm", Hash: "sha256-64/0000000000000000", Fuel: 1000UL, Enabled: false)
            )
        );
    }

    private static bool ApplyRebuildAndObserveChange(WorldFixture fixture, WorldAddonRow row) {
        var before = fixture.DefinitionBytes();
        var candidate = fixture.Server.Definition with {
            AddonsRaw = [row],
        };
        var contentHash = WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate));

        fixture.Server.EnqueueRebuild(
            request: new WorldRebuildRequest(ContentHash: contentHash, Definition: candidate, Force: true, Kind: WorldRebuildKind.Load, PathHint: "addon-no-host-rebuild-probe.world.json"),
            principal: WorldPrincipal.Console
        );
        fixture.Step();

        var after = fixture.DefinitionBytes();

        return !before.AsSpan().SequenceEqual(other: after);
    }
    private static bool ApplyAndObserveChange(WorldFixture fixture, string name) {
        var before = fixture.DefinitionBytes();

        Submit(
            fixture: fixture,
            name: name
        );

        var after = fixture.DefinitionBytes();

        return !before.AsSpan().SequenceEqual(other: after);
    }
    // The ONE predicate this law needs to script RecordingAddonHost.RefuseWhen against — a plain name lookup, kept
    // private here rather than reused from production code, whose own reuse-eligibility rule
    // (WorldAddonRuntime.RowsStructurallyEqual) compares every field, not just the name.
    private static bool HasAddonNamed(WorldDefinition definition, string name) {
        foreach (var row in definition.Addons) {
            if (string.Equals(
                a: row.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }

        return false;
    }
    private static void Submit(WorldFixture fixture, string name) {
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertAddon(
            Addon: new WorldAddonRow(Name: name, ModulePath: "unreachable.wasm", Hash: "sha256-64/0000000000000000", Fuel: 1000UL, Enabled: true),
            Principal: WorldPrincipal.Console
        ));
        fixture.Step();
    }
}

/// <summary>A scripted <see cref="IWorldAddonHost"/> that never mounts or pumps a real guest — every prepare/commit
/// call is recorded so <see cref="AddonPrepareGateLawTests"/> can assert the transaction's own shape (refusal
/// leaves nothing, commit runs once on acceptance, a throwaway plan is disposed) without a real Wasmtime guest,
/// which this project cannot reference.</summary>
internal sealed class RecordingAddonHost : IWorldAddonHost {
    /// <inheritdoc/>
    public bool AnyEverPumped => false;
    /// <summary>Gets how many times <see cref="Commit"/> was called.</summary>
    public int CommitCallCount { get; private set; }
    /// <summary>Gets every <see cref="CompleteMutation"/> call, in call order, as (token, ordinal, applied).</summary>
    public List<(long InstanceId, ushort ActOrdinal, bool Applied)> CompletedMutations { get; } = [];
    /// <summary>Gets how many times <see cref="Finish"/> was called.</summary>
    public int FinishCallCount { get; private set; }
    /// <summary>Gets every plan this host has ever produced from <see cref="TryPrepare"/>, in call order — each
    /// entry reports its own final <see cref="RecordingAddonPlan.Committed"/>/<see cref="RecordingAddonPlan.Disposed"/>
    /// state.</summary>
    public List<RecordingAddonPlan> LivePlans { get; } = [];
    /// <inheritdoc/>
    public int MountedCount => 0;
    /// <summary>Gets or sets the <see cref="RecordingAddonPlan.MountedCount"/> the NEXT accepted
    /// <see cref="TryPrepare"/> call stamps its plan with — 0 (the default) for every ordinary law; a caller-forced
    /// value lets a law drive <c>WorldServer.StageAddonContentionArrays</c>' own capacity arithmetic into an
    /// overflow without needing a real Wasmtime guest to mount that many instances.</summary>
    public int NextPlanMountedCount { get; set; }
    /// <inheritdoc/>
    public IReadOnlyList<WorldAddonReceipt> Receipts => [];
    /// <summary>Gets or sets the predicate <see cref="TryPrepare"/> consults: <see langword="true"/> refuses the
    /// candidate with a fixed reason; <see langword="null"/> (the default) accepts every candidate.</summary>
    public Func<WorldDefinition, bool>? RefuseWhen { get; set; }
    /// <summary>Gets how many times <see cref="TryPrepare"/> was called — the discriminator
    /// <see cref="AddonPrepareGateLawTests.UnrelatedSectionMutationNeverTouchesThePrepareGate"/> reads to prove a
    /// non-addon mutation never reaches this host at all.</summary>
    public int TryPrepareCallCount { get; private set; }

    /// <inheritdoc/>
    public void ApplyContributions(ulong tick) { }
    /// <inheritdoc/>
    public void Commit(IWorldAddonPreparedPlan plan) {
        ++CommitCallCount;
        ((RecordingAddonPlan)plan).Committed = true;
    }
    /// <inheritdoc/>
    public void CompleteMutation(long addonInstanceId, ushort actOrdinal, bool applied) =>
        CompletedMutations.Add(item: (addonInstanceId, actOrdinal, applied));
    /// <inheritdoc/>
    public string? DescribeUndeclaredGrantedChannels(WorldPrincipal principal, ChannelReachMask? reach, WorldChannelTable channels) => null;
    /// <inheritdoc/>
    public void Dispose() { }
    /// <inheritdoc/>
    public void Finish(IWorldAddonPreparedPlan plan) {
        ++FinishCallCount;
        ((RecordingAddonPlan)plan).Finished = true;
    }
    /// <summary>Clears every recorded call/plan — a mid-test checkpoint so a law can isolate the calls a later
    /// action makes from the setup that preceded it, without a second host/fixture.</summary>
    public void Reset() {
        CommitCallCount = 0;
        CompletedMutations.Clear();
        FinishCallCount = 0;
        TryPrepareCallCount = 0;
        LivePlans.Clear();
    }
    /// <inheritdoc/>
    public void ResolveReads(ulong tick) { }
    /// <inheritdoc/>
    public void TickAddons(ulong tick) { }
    /// <inheritdoc/>
    public bool TryPrepare(WorldDefinition? current, WorldDefinition candidate, out IWorldAddonPreparedPlan? plan, out string? reason) {
        ++TryPrepareCallCount;

        if (RefuseWhen?.Invoke(arg: candidate) == true) {
            plan = null;
            reason = "RecordingAddonHost scripted refusal";

            return false;
        }

        var recorded = new RecordingAddonPlan {
            MountedCount = NextPlanMountedCount,
        };

        LivePlans.Add(item: recorded);
        plan = recorded;
        reason = null;

        return true;
    }
}
/// <summary>The <see cref="RecordingAddonHost"/>'s own opaque plan — reports whether it was committed, disposed, or
/// (a WorldServer bug) neither/both, so a law can assert the exact linear-ownership shape a real
/// <c>PreparedAddonInstall</c> promises without constructing one.</summary>
internal sealed class RecordingAddonPlan : IWorldAddonPreparedPlan {
    /// <summary>Gets a value indicating whether <see cref="RecordingAddonHost.Commit"/> was called with this plan.</summary>
    public bool Committed { get; set; }
    /// <summary>Gets a value indicating whether <see cref="Dispose"/> was called.</summary>
    public bool Disposed { get; private set; }
    /// <summary>Gets a value indicating whether <see cref="RecordingAddonHost.Finish"/> was called with this plan.</summary>
    public bool Finished { get; set; }
    /// <inheritdoc/>
    public int MountedCount { get; init; }

    /// <inheritdoc/>
    public void Dispose() => Disposed = true;
}
