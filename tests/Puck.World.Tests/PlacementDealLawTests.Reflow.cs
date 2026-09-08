using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class PlacementDealLawTests {
    [Fact]
    public void ReflowBatchReplaysFromTheBootImageWithIdenticalLayoutAndPayment() {
        Fixtures.SkipIfReplayDirectoryUnwritable();
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        var transport = new LoopbackTransport(fixture.Server);
        var tape = new WorldReplayTape(fixture.Server, fixture.Server.Profiles, transport, [],
            Fixtures.MachineHostFactory, static (_, _) => new NullAddonHost());
        var name = $"reflow-{Guid.NewGuid():N}";
        Assert.True(tape.TryBeginRecording(name, out var refusal), refusal.ToString());
        fixture.Step();
        tape.NoteTick();
        var b = Child(fixture.Server, "b");
        transport.SubmitWorldMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, b with { Position = Child(fixture.Server, "a").Position }));
        fixture.Step();
        tape.NoteTick();
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out var reason), reason);
        transport.SubmitWorldMutation(proposal!.Mutation);
        fixture.Step();
        tape.NoteTick();
        var expected = fixture.DefinitionBytes();
        var stopped = tape.StopRecording();
        Assert.True(stopped.Verdict?.Match, stopped.VerifyFault);
        Assert.True(tape.TryBeginDrive(name, null, null, null, out refusal), refusal.ToString());
        while (tape.Mode == WorldReplayMode.Replaying) {
            tape.InjectDriveTick();
            fixture.Step();
            tape.NoteTick();
        }
        Assert.Equal(expected, fixture.DefinitionBytes());
        Assert.Equal(95, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
    }
    [Fact]
    public void UntrustedPreviewConsumesItsNormalDispatchBudget() {
        var document = ReflowDocument();
        var template = document.Placements[0];
        document = document with { PlacementRowsRaw = [template with { Deal = template.Deal! with { Reflow = new WorldPlacementReflow() } }] };
        using var fixture = Fixtures.FreshServer(document);
        fixture.Step();
        OverlapChildren(fixture);
        var peer = WorldPrincipal.Peer(generation: 1, index: 4);
        fixture.Server.Grant(new WorldGrant(peer, WorldCapability.Mutate, GrantSubject.Section(WorldSection.Placements), Exclusive: false,
            Budget: 1, KindMask: WorldMutationKindCatalog.KindsOf(WorldSection.Placements)), WorldPrincipal.Console);
        fixture.Server.Grant(new WorldGrant(peer, WorldCapability.Observe, GrantSubject.State(RowName), Exclusive: false, Budget: 1), WorldPrincipal.Console);
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, peer, out _, out var reason), reason);
        Assert.False(fixture.Server.TryPreviewReflow(TemplateId, peer, out _, out reason));
        Assert.Contains("budget", reason, StringComparison.OrdinalIgnoreCase);
        fixture.Step();
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, peer, out _, out reason), reason);
    }
    [Fact]
    public void AdvancingCurrencyPaysAtTheCommitTick() {
        var document = ReflowDocument();
        document = document with { StateRaw = document.StateRaw! with { World = [document.State[0], document.State[1] with {
            Advance = new StateAdvance(RateNumerator: 1, RateDenominator: 1, EpochTick: 0)
        }] } };
        using var fixture = Fixtures.FreshServer(document);
        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out var reason), reason);
        fixture.Step();
        fixture.Step();
        var commitTick = fixture.Server.NextInputTick;
        Assert.True(WorldStateReader.TryRead(fixture.Server.Definition, "credits", null, commitTick, out _, out var before, out _));
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.True(WorldStateReader.TryRead(fixture.Server.Definition, "credits", null, commitTick, out _, out var after, out _));
        Assert.Equal(before - 5, after);
        Assert.NotEqual(Child(fixture.Server, "a").Position, Child(fixture.Server, "b").Position);
    }
    [Fact]
    public void DuplicateSiblingSlotsAndNestedDealsAreRejected() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        var a = Child(fixture.Server, "a");
        var b = Child(fixture.Server, "b");
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, b with { DealSlot = a.DealSlot }));
        fixture.Step();
        Assert.Equal(b.DealSlot, Child(fixture.Server, "b").DealSlot);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, b with {
            Deal = fixture.Server.Definition.Placements[0].Deal,
            Distribution = fixture.Server.Definition.Placements[0].Distribution
        }));
        fixture.Step();
        Assert.Null(Child(fixture.Server, "b").Deal);
    }

    [Theory]
    [InlineData(150, true)]
    [InlineData(20, true)]
    [InlineData(2, false)]
    public void ReflowChecksCurrentSolvencyAndExactDebit(long balance, bool succeeds) {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out var reason), reason);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(WorldPrincipal.Console, "credits", WorldStateRow.SlotKey.Value, balance, WorldDocumentWriteKind.Set));
        fixture.Step();
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(succeeds, Child(fixture.Server, "a").Position != Child(fixture.Server, "b").Position);
        Assert.Equal(balance - (succeeds ? 5 : 0), WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyNearbySpatialPlacementsInvalidateAnotherCourtsPreview(bool blocks) {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out var reason), reason);
        var unrelated = Child(fixture.Server, "a") with { Id = "chair", DealSlot = null, Spatial = blocks ? ReflowSpatial() : null };
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, unrelated));
        fixture.Step();
        Assert.Contains(fixture.Server.Definition.Placements, p => p.Id == "chair");
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(!blocks, Child(fixture.Server, "a").Position != Child(fixture.Server, "b").Position);
    }

    [Fact]
    public async Task BackgroundReflowReturnsAnImmutableProposalWithoutWriting() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture);
        var before = fixture.Server.Definition;
        Assert.True(fixture.Server.TryStartReflowPreview(TemplateId, WorldPrincipal.Console, out var pending, out var reason), reason);
        var result = await pending!;
        Assert.NotNull(result.Proposal);
        Assert.Empty(result.Reason);
        Assert.Same(before, fixture.Server.Definition);
        fixture.Server.EnqueueMutation(result.Proposal.Mutation);
        fixture.Step();
        Assert.NotEqual(Child(fixture.Server, "a").Position, Child(fixture.Server, "b").Position);
    }
    [Fact]
    public void GuardedReflowSurvivesUnrelatedStateWritesWireEncodingAndJournalReconstruction() {
        var document = ReflowDocument();
        document = document with { StateRaw = document.StateRaw! with { World = [.. document.State,
            new WorldStateRow(CellName.Parse("clock"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0)])] } };
        using var fixture = Fixtures.FreshServer(document);
        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out var reason), reason);
        Assert.True(WorldSubmissionCodec.TryEncodeMutation(proposal!.Mutation, out var bytes, out var failure), failure.ToString());
        Assert.True(WorldSubmissionCodec.TryDecodeMutation(bytes, out var decoded, out failure), failure.ToString());
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(WorldPrincipal.Console, "clock", WorldStateRow.SlotKey.Value, 1, WorldDocumentWriteKind.Add));
        fixture.Step();
        fixture.Server.EnqueueMutation(decoded!);
        fixture.Step();
        Assert.Equal(95, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(WorldPrincipal.Console, "clock", WorldStateRow.SlotKey.Value, 1, WorldDocumentWriteKind.Add));
        fixture.Step();
        fixture.Server.EnqueueUndo(1, WorldPrincipal.Console);
        fixture.Step();
        Assert.NotEqual(Child(fixture.Server, "a").Position, Child(fixture.Server, "b").Position);
        Assert.Equal(95, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
        fixture.Server.EnqueueUndo(1, WorldPrincipal.Console);
        fixture.Step();
        Assert.Equal(Child(fixture.Server, "a").Position, Child(fixture.Server, "b").Position);
        Assert.Equal(100, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
    }

    [Fact]
    public void MovingTheCourtRebuildsItsNavigationInTheSameFrameAsItsChildren() {
        var document = ReflowDocument() with { NavigationRaw = new WorldNavigationSection(Domains: [new WorldNavigationDomain(
            Name: "court", Kind: WorldNavigationKind.Volume, Origin: new System.Numerics.Vector3(0, 2, 0),
            CellSize: 1, Width: 2, Depth: 2, AgentRadius: .1f, ArrivalDistance: .2f, MaxExpandedNodes: 4, MaxPathNodes: 4, Parent: TemplateId)]) };
        using var fixture = Fixtures.FreshServer(document);
        fixture.Step();
        var before = fixture.Server.Population.DescribeNavigation();
        var template = fixture.Server.Definition.Placements[0];
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, template with {
            Position = new System.Numerics.Vector3(20, 1, 30), YawDegrees = 90
        }));
        fixture.Step();
        Assert.Equal(new System.Numerics.Vector3(20, 1, 30), fixture.Server.Definition.PlacementFrames[Child(fixture.Server, "a").Id].Position);
        var after = fixture.Server.Population.DescribeNavigation();
        Assert.NotEqual(before, after);
        Assert.Contains("origin=(20,3,30)", after);
        using var rebuilt = Fixtures.FreshServer(fixture.Server.Definition);
        Assert.Equal(after, rebuilt.Server.Population.DescribeNavigation());
    }
    [Theory]
    [InlineData(1, 100, 0)]
    [InlineData(4096, 2, 0)]
    [InlineData(4096, 100, 98)]
    public void ReflowRefusesExhaustedWorkInsufficientFundsAndClippedPayments(int budget, long balance, long minimum) {
        var document = ReflowDocument();
        var template = document.Placements[0];
        document = document with {
            PlacementRowsRaw = [template with { Deal = template.Deal! with { Reflow = template.Deal.Reflow! with { CandidateBudget = budget } } }],
            StateRaw = new WorldStateSection(World: [document.State[0], document.State[1] with {
                Min = minimum, Cells = [new StateCell(WorldStateRow.SlotKey, balance)]
            }])
        };
        using var fixture = Fixtures.FreshServer(document);
        fixture.Step();
        OverlapChildren(fixture);
        var before = fixture.Server.Definition;
        Assert.False(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out var reason));
        Assert.Null(proposal);
        Assert.NotEmpty(reason);
        Assert.Same(before, fixture.Server.Definition);
    }

    [Fact]
    public void ReflowCannotCommitAfterPlacementAuthorityIsRevoked() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out var reason), reason);
        fixture.Server.Revoke(new WorldGrant(WorldPrincipal.Console, WorldCapability.Mutate, GrantSubject.Section(WorldSection.Placements), Exclusive: false), WorldPrincipal.Console);
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(Child(fixture.Server, "a").Position, Child(fixture.Server, "b").Position);
        Assert.Equal(100, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
    }
    private static IReadOnlyList<WorldPlacementSpatialVolume> ReflowSpatial(bool pinned = false) => [
        new("body", WorldPlacementSpatialRole.Occupation,
            new WorldSpatialShape(WorldSpatialShapeKind.Box, System.Numerics.Vector3.Zero, new System.Numerics.Vector3(.4f)), Pinned: pinned)
    ];

    private static WorldDefinition ReflowDocument() {
        var template = Template(new WorldPlacementDeal(RowName, Preserve: new WorldPlacementDealPreserve(Transform: true, Facets: true),
            Reflow: new WorldPlacementReflow(CostPerMove: 5, CostRow: "credits"))) with { Spatial = ReflowSpatial() };
        var document = Document(template, AccountsRow("a", "b"));
        return document with { StateRaw = new WorldStateSection(World: [.. document.State,
            new WorldStateRow(Name: CellName.Parse("credits"), Kind: CellKind.Int, Min: 0, Max: 1000, Cells: [new StateCell(WorldStateRow.SlotKey, 100)])]) };
    }

    private static void OverlapChildren(WorldFixture fixture, bool pinned = false) {
        var a = Child(fixture.Server, "a");
        var b = Child(fixture.Server, "b");
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, b with {
            Position = a.Position, Spatial = ReflowSpatial(pinned)
        }));
        if (pinned) { fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, a with { Spatial = ReflowSpatial(true) })); }
        fixture.Step();
    }

    [Fact]
    public void ReflowPreviewsWithoutWritingThenMovesAndChargesAsOneEdit() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture);
        var before = fixture.Server.Definition;
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out var reason), reason);
        Assert.Same(before, fixture.Server.Definition);
        Assert.Equal(1, proposal!.Moved);
        Assert.Equal(5, proposal.Cost);
        fixture.Server.EnqueueMutation(proposal.Mutation);
        fixture.Step();
        Assert.NotEqual(Child(fixture.Server, "a").Position, Child(fixture.Server, "b").Position);
        Assert.Equal(95, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
    }

    [Fact]
    public void StaleReflowChangesNeitherLayoutNorBalance() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out var reason), reason);
        fixture.Server.EnqueueMutation(Upsert("c"));
        fixture.Step();
        var before = fixture.Server.Definition;
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(Child(fixture.Server, "a").Position, Child(fixture.Server, "b").Position);
        Assert.Equal(100, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
        Assert.Equal(before.Placements, fixture.Server.Definition.Placements);
    }

    [Fact]
    public void OverlappingPinnedChildrenRefuseWithoutAProposal() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture, pinned: true);
        var before = fixture.Server.Definition;
        Assert.False(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var proposal, out _));
        Assert.Null(proposal);
        Assert.Same(before, fixture.Server.Definition);
    }
}
