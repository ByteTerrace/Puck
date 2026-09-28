using Puck.World.Server;
using Puck.Testing;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class PlacementDealLawTests {
    [Fact]
    public void ReflowBatchReplaysFromTheBootImageWithIdenticalLayoutAndPayment() {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-");
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            fixture.Server,
            fixture.Server.Profiles,
            transport,
            [],
            Fixtures.MachineHostFactory,
            static (_, _) => new NullAddonHost(),
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath)
        );
        var name = $"reflow-{Guid.NewGuid():N}";

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: refusal.ToString()
        );
        fixture.Step();
        tape.NoteTick();
        var b = Child(
            fixture.Server,
            "b"
        );

        transport.SubmitWorldMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            b with {
                Position = Child(
                fixture.Server,
                "a"
            ).Position,
            }
        ));
        fixture.Step();
        tape.NoteTick();
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                TemplateId,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        transport.SubmitWorldMutation(proposal!.Mutation);
        fixture.Step();
        tape.NoteTick();
        var expected = fixture.DefinitionBytes();
        var stopped = tape.StopRecording();

        Assert.True(
            condition: stopped.Verdict?.Match,
            userMessage: stopped.VerifyFault
        );
        Assert.True(
            condition: tape.TryBeginDrive(
                documentPath: null,
                forkName: null,
                name: name,
                refusal: out refusal,
                toTick: null
            ),
            userMessage: refusal.ToString()
        );
        while (tape.Mode == WorldReplayMode.Replaying) {
            tape.InjectDriveTick();
            fixture.Step();
            tape.NoteTick();
        }
        Assert.Equal(
            expected,
            fixture.DefinitionBytes()
        );
        Assert.Equal(
            95,
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
    }
    [Fact]
    public void UntrustedPreviewConsumesItsNormalDispatchBudget() {
        var document = ReflowDocument();
        var template = document.Placements[0];

        document = document with { PlacementRowsRaw = [template with { Deal = template.Deal! with { Reflow = new WorldPlacementReflow() } }] };
        using var fixture = Fixtures.FreshServer(document);

        fixture.Step();
        OverlapChildren(fixture);
        var peer = Principal.Peer(
            generation: 1,
            index: 4
        );

        fixture.Server.Grant(
            new WorldGrant(
                peer,
                WorldCapability.Mutate,
                GrantSubject.Section(section: WorldSection.Placements),
                Exclusive: false,
                Budget: 1,
                KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements)
            ),
            Principal.Console
        );
        fixture.Server.Grant(
            new WorldGrant(
                peer,
                WorldCapability.Observe,
                GrantSubject.State(name: RowName),
                Exclusive: false,
                Budget: 1
            ),
            Principal.Console
        );
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                principal: peer,
                proposal: out _,
                reason: out var reason,
                templateId: TemplateId
            ),
            userMessage: reason
        );
        Assert.False(condition: fixture.Server.TryPreviewReflow(
            principal: peer,
            proposal: out _,
            reason: out reason,
            templateId: TemplateId
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "budget"
        );
        fixture.Step();
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                principal: peer,
                proposal: out _,
                reason: out reason,
                templateId: TemplateId
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void AdvancingCurrencyPaysAtTheCommitTick() {
        var document = ReflowDocument();

        document = document with {
            StateRaw = document.StateRaw! with {
                World = [document.State[0], document.State[1] with {
            Advance = new StateAdvance(
                PerSecondDenominator: 1,
                PerSecondNumerator: 1
            ),
        }],
            },
        };
        using var fixture = Fixtures.FreshServer(document);

        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                TemplateId,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        fixture.Step();
        fixture.Step();
        var commitTick = fixture.Server.NextInputTick;

        Assert.True(condition: WorldStateReader.TryRead(
            fixture.Server.Definition,
            "credits",
            null,
            commitTick,
            fixture.Server.CompletedEngineTicks,
            out _,
            out var before,
            out _
        ));
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.True(condition: WorldStateReader.TryRead(
            fixture.Server.Definition,
            "credits",
            null,
            commitTick,
            fixture.Server.CompletedEngineTicks,
            out _,
            out var after,
            out _
        ));
        Assert.Equal(
            actual: after,
            expected: (before - 5)
        );
        Assert.NotEqual(
            Child(
                fixture.Server,
                "a"
            ).Position,
            Child(
                fixture.Server,
                "b"
            ).Position
        );
    }
    [Fact]
    public void DuplicateSiblingSlotsAndNestedDealsAreRejected() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        var a = Child(
            fixture.Server,
            "a"
        );
        var b = Child(
            fixture.Server,
            "b"
        );

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            b with { DealSlot = a.DealSlot }
        ));
        fixture.Step();
        Assert.Equal(
            b.DealSlot,
            Child(
                fixture.Server,
                "b"
            ).DealSlot
        );
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            b with {
                Deal = fixture.Server.Definition.Placements[0].Deal,
                Distribution = fixture.Server.Definition.Placements[0].Distribution,
            }
        ));
        fixture.Step();
        Assert.Null(@object: Child(
            fixture.Server,
            "b"
        ).Deal);
    }
    [InlineData(150, true)]
    [InlineData(20, true)]
    [InlineData(2, false)]
    [Theory]
    public void ReflowChecksCurrentSolvencyAndExactDebit(long balance, bool succeeds) {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                TemplateId,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
            Principal.Console,
            "credits",
            WorldStateRow.SlotKey.Value,
            balance,
            WorldDocumentWriteKind.Set
        ));
        fixture.Step();
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(
            succeeds,
            (Child(
                fixture.Server,
                "a"
            ).Position != Child(
                fixture.Server,
                "b"
            ).Position)
        );
        Assert.Equal(
            (balance - (succeeds
            ? 5
            : 0)),
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void OnlyNearbySpatialPlacementsInvalidateAnotherCourtsPreview(bool blocks) {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                TemplateId,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        var unrelated = Child(
            fixture.Server,
            "a"
        ) with {
            Id = "chair",
            DealSlot = null,
            Spatial = (blocks
            ? ReflowSpatial()
            : null),
        };

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            unrelated
        ));
        fixture.Step();
        Assert.Contains(
            collection: fixture.Server.Definition.Placements,
            filter: p => (p.Id == "chair")
        );
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(
            !blocks,
            (Child(
                fixture.Server,
                "a"
            ).Position != Child(
                fixture.Server,
                "b"
            ).Position)
        );
    }
    [Fact]
    public async Task BackgroundReflowReturnsAnImmutableProposalWithoutWriting() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        OverlapChildren(fixture);
        var before = fixture.Server.Definition;

        Assert.True(
            condition: fixture.Server.TryStartReflowPreview(
                TemplateId,
                Principal.Console,
                out var pending,
                out var reason
            ),
            userMessage: reason
        );
        var result = await pending!;

        Assert.NotNull(@object: result.Proposal);
        Assert.Empty(value: result.Reason);
        Assert.Same(
            before,
            fixture.Server.Definition
        );
        fixture.Server.EnqueueMutation(result.Proposal.Mutation);
        fixture.Step();
        Assert.NotEqual(
            Child(
                fixture.Server,
                "a"
            ).Position,
            Child(
                fixture.Server,
                "b"
            ).Position
        );
    }
    [Fact]
    public void GuardedReflowSurvivesUnrelatedStateWritesWireEncodingAndJournalReconstruction() {
        var document = ReflowDocument();

        document = document with {
            StateRaw = document.StateRaw! with {
                World = [.. document.State,
            new WorldStateRow(
                CellName.Parse(candidate: "clock"),
                CellKind.Int,
                Cells: [new StateCell(
                        WorldStateRow.SlotKey,
                        CellValue.Int(value: 0)
                    )]
            )],
            },
        };
        using var fixture = Fixtures.FreshServer(document);

        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                TemplateId,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: WorldSubmissionCodec.TryEncodeMutation(
                proposal!.Mutation,
                out var bytes,
                out var failure
            ),
            userMessage: failure.ToString()
        );
        Assert.True(
            condition: WorldSubmissionCodec.TryDecodeMutation(
                bytes: bytes,
                failure: out failure,
                mutation: out var decoded
            ),
            userMessage: failure.ToString()
        );
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
            Principal.Console,
            "clock",
            WorldStateRow.SlotKey.Value,
            1,
            WorldDocumentWriteKind.Add
        ));
        fixture.Step();
        fixture.Server.EnqueueMutation(decoded!);
        fixture.Step();
        Assert.Equal(
            95,
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
            Principal.Console,
            "clock",
            WorldStateRow.SlotKey.Value,
            1,
            WorldDocumentWriteKind.Add
        ));
        fixture.Step();
        fixture.Server.EnqueueUndo(
            1,
            Principal.Console
        );
        fixture.Step();
        Assert.NotEqual(
            Child(
                fixture.Server,
                "a"
            ).Position,
            Child(
                fixture.Server,
                "b"
            ).Position
        );
        Assert.Equal(
            95,
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
        fixture.Server.EnqueueUndo(
            1,
            Principal.Console
        );
        fixture.Step();
        Assert.Equal(
            Child(
                fixture.Server,
                "a"
            ).Position,
            Child(
                fixture.Server,
                "b"
            ).Position
        );
        Assert.Equal(
            100,
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
    }
    [Fact]
    public void MovingTheCourtRebuildsItsNavigationInTheSameFrameAsItsChildren() {
        var document = ReflowDocument() with {
            NavigationRaw = new WorldNavigationSection(Domains: [new WorldNavigationDomain(
                Name: "court",
                Kind: WorldNavigationKind.Volume,
                Origin: new System.Numerics.Vector3(
                    x: 0,
                    y: 2,
                    z: 0
                ),
                CellSize: 1,
                Width: 2,
                Depth: 2,
                AgentRadius: .1f,
                ArrivalDistance: .2f,
                MaxExpandedNodes: 4,
                MaxPathNodes: 4,
                Parent: TemplateId
            )]),
        };
        using var fixture = Fixtures.FreshServer(document);

        fixture.Step();
        var before = fixture.Server.Population.DescribeNavigation();
        var template = fixture.Server.Definition.Placements[0];

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            template with {
                Position = new System.Numerics.Vector3(
                x: 20,
                y: 1,
                z: 30
            ),
                YawDegrees = 90,
            }
        ));
        fixture.Step();
        Assert.Equal(
            new System.Numerics.Vector3(
                x: 20,
                y: 1,
                z: 30
            ),
            fixture.Server.Definition.PlacementFrames[Child(
                fixture.Server,
                "a"
            ).Id].Position
        );
        var after = fixture.Server.Population.DescribeNavigation();

        Assert.NotEqual(
            actual: after,
            expected: before
        );
        Assert.Contains(
            actualString: after,
            expectedSubstring: "origin=(20,3,30)"
        );
        using var rebuilt = Fixtures.FreshServer(fixture.Server.Definition);

        Assert.Equal(
            after,
            rebuilt.Server.Population.DescribeNavigation()
        );
    }
    [InlineData(1, 100, 0)]
    [InlineData(4096, 2, 0)]
    [InlineData(4096, 100, 98)]
    [Theory]
    public void ReflowRefusesExhaustedWorkInsufficientFundsAndClippedPayments(int budget, long balance, long minimum) {
        var document = ReflowDocument();
        var template = document.Placements[0];

        document = document with {
            PlacementRowsRaw = [template with { Deal = template.Deal! with { Reflow = template.Deal.Reflow! with { CandidateBudget = budget } } }],
            StateRaw = new WorldStateSection(World: [document.State[0], document.State[1] with {
                Min = minimum, Cells = [new StateCell(
                    WorldStateRow.SlotKey,
                    CellValue.Int(value: balance)
                )],
            }]),
        };
        using var fixture = Fixtures.FreshServer(document);

        fixture.Step();
        OverlapChildren(fixture);
        var before = fixture.Server.Definition;

        Assert.False(condition: fixture.Server.TryPreviewReflow(
            TemplateId,
            Principal.Console,
            out var proposal,
            out var reason
        ));
        Assert.Null(@object: proposal);
        Assert.NotEmpty(collection: reason);
        Assert.Same(
            before,
            fixture.Server.Definition
        );
    }
    [Fact]
    public void ReflowCannotCommitAfterPlacementAuthorityIsRevoked() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                TemplateId,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        fixture.Server.Revoke(
            new WorldGrant(
                Principal.Console,
                WorldCapability.Mutate,
                GrantSubject.Section(section: WorldSection.Placements),
                Exclusive: false
            ),
            Principal.Console
        );
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(
            Child(
                fixture.Server,
                "a"
            ).Position,
            Child(
                fixture.Server,
                "b"
            ).Position
        );
        Assert.Equal(
            100,
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
    }

    private static IReadOnlyList<WorldPlacementSpatialVolume> ReflowSpatial(bool pinned = false) => [
        new(
            "body",
            WorldPlacementSpatialRole.Occupation,
            new WorldSpatialShape(
                WorldSpatialShapeKind.Box,
                System.Numerics.Vector3.Zero,
                new System.Numerics.Vector3(value: .4f)
            ),
            Pinned: pinned
        )
    ];
    private static WorldDefinition ReflowDocument() {
        var template = Template(new WorldPlacementDeal(
            RowName,
            Preserve: new WorldPlacementDealPreserve(
                Transform: true,
                Facets: true
            ),
            Reflow: new WorldPlacementReflow(
                CostPerMove: 5,
                CostRow: "credits"
            )
        )) with { Spatial = ReflowSpatial() };
        var document = Document(
            template,
            AccountsRow(
                "a",
                "b"
            )
        );

        return document with {
            StateRaw = new WorldStateSection(World: [.. document.State,
            new WorldStateRow(
                Name: CellName.Parse(candidate: "credits"),
                Kind: CellKind.Int,
                Min: 0,
                Max: 1000,
                Cells: [new StateCell(
                        WorldStateRow.SlotKey,
                        CellValue.Int(value: 100)
                    )]
            )]),
        };
    }
    private static void OverlapChildren(WorldFixture fixture, bool pinned = false) {
        var a = Child(
            fixture.Server,
            "a"
        );
        var b = Child(
            fixture.Server,
            "b"
        );

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            b with {
                Position = a.Position,
                Spatial = ReflowSpatial(pinned: pinned),
            }
        ));
        if (pinned) {
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            a with { Spatial = ReflowSpatial(pinned: true) }
        ));
        }
        fixture.Step();
    }

    [Fact]
    public void ReflowPreviewsWithoutWritingThenMovesAndChargesAsOneEdit() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        OverlapChildren(fixture);
        var before = fixture.Server.Definition;

        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                TemplateId,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        Assert.Same(
            before,
            fixture.Server.Definition
        );
        Assert.Equal(
            1,
            proposal!.Moved
        );
        Assert.Equal(
            5,
            proposal.Cost
        );
        fixture.Server.EnqueueMutation(proposal.Mutation);
        fixture.Step();
        Assert.NotEqual(
            Child(
                fixture.Server,
                "a"
            ).Position,
            Child(
                fixture.Server,
                "b"
            ).Position
        );
        Assert.Equal(
            95,
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
    }
    [Fact]
    public void StaleReflowChangesNeitherLayoutNorBalance() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        OverlapChildren(fixture);
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                TemplateId,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        fixture.Server.EnqueueMutation(Upsert(key: "c"));
        fixture.Step();
        var before = fixture.Server.Definition;

        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(
            Child(
                fixture.Server,
                "a"
            ).Position,
            Child(
                fixture.Server,
                "b"
            ).Position
        );
        Assert.Equal(
            100,
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
        Assert.Equal(
            before.Placements,
            fixture.Server.Definition.Placements
        );
    }
    [Fact]
    public void OverlappingPinnedChildrenRefuseWithoutAProposal() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        OverlapChildren(
            fixture,
            pinned: true
        );
        var before = fixture.Server.Definition;

        Assert.False(condition: fixture.Server.TryPreviewReflow(
            TemplateId,
            Principal.Console,
            out var proposal,
            out _
        ));
        Assert.Null(@object: proposal);
        Assert.Same(
            before,
            fixture.Server.Definition
        );
    }
}
