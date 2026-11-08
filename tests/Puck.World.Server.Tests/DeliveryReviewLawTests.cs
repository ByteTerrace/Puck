using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Counterexamples from the step-one delivery review, through live views and actual session deliveries.</summary>
public sealed class DeliveryReviewLawTests {
    private static WorldAdmissionEntry Admission(WorldDisclosureTier tier) => new(Domain: "*", Disclosure: tier,
        Mode: WorldAdmissionTrustMode.FederatedAuthority,
        Grants: [new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: GrantSubject.All, Budget: 100)]);
    private static WorldStateRow Row(long value = 3, HiddenCells hidden = HiddenCells.Omit, bool secret = true) => new(
        Name: CellName.Parse("hand"), Kind: CellKind.Int, Visibility: new StateVisibility(Hidden: hidden),
        Cells: new[] { new StateCell(CellName.Parse("pub"), CellValue.Int(value)) }
            .Concat(secret ? [new StateCell(CellName.Parse("secret"), CellValue.Int(7), Visibility: new StateVisibility(Readers: ["seat1"]))] : []).ToArray());
    private static WorldDefinition Document(WorldStateRow? row = null, WorldDisclosureTier tier = WorldDisclosureTier.Replica) =>
        Fixtures.BuildDocument() with { Admission = [Admission(tier)], StateRaw = new WorldStateSection(World: [row ?? Row()]) };
    private static WorldRecipientView View(WorldFixture fixture, WorldDisclosureImages images, WorldDisclosureTier tier = WorldDisclosureTier.Replica) =>
        new(() => fixture.Server.Definition, () => fixture.Server.Arena, Principal.Seat(1), tier, () => true, images,
            fixture.Server.AuthorityIdentity, () => fixture.Server.DeliveryTime);
    private static WorldSessionObservation Observe(WorldFixture fixture, Replica sink) {
        var observation = fixture.Server.TryObserveAsSession(refusal: out var refusal, sink: sink, sourceAuthority: "reader");
        Assert.NotNull(observation);
        Assert.True(string.IsNullOrEmpty(refusal), refusal);
        return observation!;
    }

    [Theory]
    [InlineData(WorldDisclosureTier.Replica)]
    [InlineData(WorldDisclosureTier.Presentation)]
    public void PublicAndAdmittedPhysicalFieldsReachBothTiersWhileExcludedFieldsDoNot(WorldDisclosureTier tier) {
        var region = new WorldFieldsSection(Name: "ground",
            Lattice: new WorldFieldLatticeDefinition(new DocumentVector3(0, 0, 0), 1, 2, 2, 2, StepEveryTicks: 1),
            Fields: [new WorldFieldRow(Name: "publicField", Min: 0, Max: 100), new WorldFieldRow(Name: "admittedField", Min: 0, Max: 100),
                new WorldFieldRow(Name: "excludedField", Min: 0, Max: 100)],
            Reactions: [.. new[] { "publicField", "admittedField", "excludedField" }.Select(name =>
                new WorldReaction.Transform(When: [], Then: [new WorldFieldWrite(Field: name, Op: WorldFieldWriteOp.Add, Value: 1)]))]);
        var section = WorldFieldsSection.ToStateSection([region]);
        section = section with { World = section.World!.Select(row => row.Name.Value switch {
            "admittedField" => row with { Visibility = new StateVisibility(ReadersFrom: "readers") },
            "excludedField" => row with { Visibility = new StateVisibility(Readers: ["seat1"]) },
            _ => row,
        }).Append(new WorldStateRow(Name: CellName.Parse("readers"), Kind: CellKind.Text, Domain: StateDomain.Keys.Instance,
            Cells: [new StateCell(CellName.Parse("allowed"), CellValue.Text("session:0:1"))])).ToArray() };
        using var fixture = Fixtures.FreshServer(Document(tier: tier) with { StateRaw = section });
        var sink = new Replica();
        using var observation = Observe(fixture, sink);
        Assert.Equal("session:0:1", observation.Session.Describe());
        Assert.Contains(sink.Deltas, delta => delta.FieldName == "publicField");
        Assert.Contains(sink.Deltas, delta => delta.FieldName == "admittedField");
        Assert.DoesNotContain(sink.Deltas, delta => delta.FieldName == "excludedField");
        sink.Deltas.Clear();
        fixture.Step();
        Assert.Contains(sink.Deltas, delta => delta.FieldName == "publicField" && delta.Raw != 0);
        Assert.Contains(sink.Deltas, delta => delta.FieldName == "admittedField" && delta.Raw != 0);
        Assert.DoesNotContain(sink.Deltas, delta => delta.FieldName == "excludedField");
    }

    [Fact]
    public void ARestrictedAdministrativeWriteBeforeTheFirstTickCannotPoisonTheSharedImage() {
        using var fixture = Fixtures.FreshServer(Document());
        var sink = new Replica();
        using var observation = Observe(fixture, sink);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "hand", "pub", 11, WorldDocumentWriteKind.Set));
        Assert.True(fixture.Server.DrainAdministrative());
        Assert.Equal(0UL, fixture.Server.CompletedTick);
        Assert.Equal(11L, sink.Read("hand", "pub"));
        fixture.Step();
        Assert.Equal(11L, sink.Read("hand", "pub"));
    }

    [Fact]
    public void AReadableWriteMissedByAThrottledStreamCannotUseTheNextWindowsKeyProof() {
        using var fixture = Fixtures.FreshServer(Document());
        var images = new WorldDisclosureImages();
        var view = View(fixture, images);
        var changes = new ArenaChangedKeys();
        images.BeginTick(changes);
        Assert.Equal(3L, view.Image(0).Cells[0].Value.Raw);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "hand", "pub", 11, WorldDocumentWriteKind.Set));
        Assert.True(fixture.Server.DrainAdministrative());
        changes.Clear();
        images.BeginTick(changes);
        Assert.Equal(11L, view.Image(0).Cells[0].Value.Raw);
    }

    [Fact]
    public void AStateClockCorrectionIsDeliveredEvenWhenItsSourceIsNotAnObservation() {
        var document = Document(tier: WorldDisclosureTier.Presentation) with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(Name: CellName.Parse("phase"), Kind: CellKind.Fixed,
                Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Fixed(0))])]),
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", State: "phase")]),
            RenderRaw = new WorldRenderDefaults(Atmosphere: new WorldRenderAtmosphere(Fog: new WorldRenderFog(
                Density: new BindableScalar(keys: new WorldKeyTrack<float>(clock: "day", keys: [
                    new WorldKey<float>(At: 0, Value: 0, Ease: WorldEase.Linear), new WorldKey<float>(At: 0.5, Value: 0.1f, Ease: WorldEase.Linear)])))))
        };
        using var fixture = Fixtures.FreshServer(document);
        var images = new WorldDisclosureImages();
        var view = View(fixture, images, WorldDisclosureTier.Presentation);
        var feed = new WorldProjectionFeed(Principal.Seat(1));
        var cursor = new WorldDeliveryCursor();
        Assert.Equal(WorldProjectionDeliveryKind.Document, feed.Compose(view, cursor).Kind);
        Assert.Null(view.Image(0).Observation);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "phase", WorldStateRow.SlotKey.Value, 16384, WorldDocumentWriteKind.Set));
        Assert.True(fixture.Server.DrainAdministrative());
        var correction = feed.Compose(view, cursor);
        Assert.Equal(WorldProjectionDeliveryKind.Delta, correction.Kind);
        Assert.NotEqual(0UL, Assert.Single(correction.Projection!.Timeline!.Clocks!).Anchor!.Phase);
        Assert.Equal(2L, cursor.Sequence);
        Assert.Equal(WorldProjectionDeliveryKind.None, feed.Compose(view, cursor).Kind);
    }

    [Fact]
    public void WholeReplicaRowsKeepTheirStorageMetadata() {
        var row = Row() with { Visibility = null, Domain = new StateDomain.Ring(Capacity: 8),
            Cells = [new StateCell(CellName.Parse("0"), CellValue.Int(3))], HistoryCursor = 12 };
        using var fixture = Fixtures.FreshServer(Document(row));
        var view = View(fixture, new WorldDisclosureImages());
        var expected = WorldStateDisclosure.Disclose(fixture.Server.Definition, fixture.Server.Arena, Principal.Seat(1)).Definition.AuthoredState[0];
        var actual = view.Definition.AuthoredState[0];
        Assert.NotEqual(0, expected.HistoryCursor);
        Assert.Equal(expected.DrawCursor, actual.DrawCursor);
        Assert.Equal(expected.HistoryCursor, actual.HistoryCursor);
        Assert.Equal(expected.ReservedCapacity, actual.ReservedCapacity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AuthorityAndAdmittedRecipientsCannotSeedEachOthersStorageMetadata(bool authorityFirst) {
        var row = Row() with { Domain = new StateDomain.Ring(Capacity: 8), HistoryCursor = 12,
            Cells = [new StateCell(CellName.Parse("0"), CellValue.Int(3), Visibility: new StateVisibility(Readers: ["seat2"]))] };
        using var fixture = Fixtures.FreshServer(Document(row));
        var images = new WorldDisclosureImages();
        var recipient = View(fixture, images);
        var authority = new WorldRecipientView(() => fixture.Server.Definition, () => fixture.Server.Arena,
            Principal.Console, WorldDisclosureTier.Replica, () => true, images, fixture.Server.AuthorityIdentity, () => fixture.Server.DeliveryTime);
        var authorityCursor = new WorldDeliveryCursor();
        var recipientCursor = new WorldDeliveryCursor();
        var first = (authorityFirst ? authorityCursor.DeliverDefinition(authority) : recipientCursor.DeliverDefinition(recipient))!;
        var second = (authorityFirst ? recipientCursor.DeliverDefinition(recipient) : authorityCursor.DeliverDefinition(authority))!;
        Assert.Equal(12, (authorityFirst ? first : second).AuthoredState[0].HistoryCursor);
        Assert.Equal(0, (authorityFirst ? second : first).AuthoredState[0].HistoryCursor);
    }

    [Theory]
    [InlineData(HiddenCells.Omit)]
    [InlineData(HiddenCells.Count)]
    [InlineData(HiddenCells.Placeholder)]
    public void ReplicaMetadataPrimingAndReplacementNeverDependOnTheCurrentHiddenCount(HiddenCells policy) {
        static (long[] Versions, bool[] Metadata, long[] Quiet) Run(HiddenCells hidden, bool secret) {
            using var fixture = Fixtures.FreshServer(Document(Row(hidden: hidden, secret: secret)));
            var sink = new Replica();
            using var observation = Observe(fixture, sink);
            var metadata = new List<bool> { sink.State.Observation(0) is not null };
            // A public definition change replaces StateRaw in the disclosed document just as a placement refresh
            // does; its metadata must be complete before a subsequent quiet tick.
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(Principal.Console,
                new WorldStateRow(Name: CellName.Parse("public"), Kind: CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(4))])));
            fixture.Step();
            metadata.Add(sink.State.Observation(0) is not null);
            var before = sink.Versions.ToArray();
            fixture.Step();
            return (before, metadata.ToArray(), sink.Versions.Skip(before.Length).ToArray());
        }
        var zero = Run(policy, false);
        var one = Run(policy, true);
        Assert.Equal(zero.Versions, one.Versions);
        Assert.Equal(zero.Quiet, one.Quiet);
        Assert.All(zero.Metadata.Concat(one.Metadata), Assert.True);
        Assert.All(one.Quiet, version => Assert.Equal(one.Versions[^1], version));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplicaRestoreCompletesItsObservationHeadersBeforeAnyFollowingTick(bool secret) {
        using var fixture = Fixtures.FreshServer(Document(Row(hidden: HiddenCells.Count, secret: secret)));
        var sink = new Replica();
        using var observation = Observe(fixture, sink);
        Assert.True(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), reason);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "hand", "pub", 11, WorldDocumentWriteKind.Set));
        fixture.Step();
        Assert.Equal(11L, sink.Read("hand", "pub"));
        // Restore delivers a document outside the tick, with no ordinary snapshot following it.
        fixture.Server.RestoreCheckpoint(checkpoint!);
        Assert.Equal(3L, sink.Read("hand", "pub"));
        Assert.NotNull(sink.State.Observation(0));
        Assert.Equal(secret ? 1 : 0, sink.State.Observation(0)!.HiddenCount);
        var sequence = sink.Versions[^1];
        fixture.Step();
        Assert.Equal(sequence, sink.Versions[^1]);
    }

    private sealed class Replica : IClientSink {
        private WorldDefinition m_definition = WorldProjection.Undisclosed;
        public WorldDocumentStateView State { get; }
        public List<FieldCellDelta> Deltas { get; } = [];
        public List<long> Versions { get; } = [];
        public Replica() => State = new(() => m_definition);
        public long Read(string row, string key) {
            Assert.True(State.TryResolveRow(row, out var ordinal));
            Assert.True(State.TryRead(ordinal, key, false, 0, 0, out var sample));
            return sample.Value.Raw;
        }
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
            m_definition = definition;
            Versions.Add(version.Sequence);
            State.TryResolveRow("hand", out _);
        }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => DeliverDefinition(definition, version);
        public void DeliverSnapshot(in WorldSnapshot snapshot) {
            Deltas.AddRange(snapshot.FieldCells.ToArray());
            Versions.Add(snapshot.Revision);
            State.ApplyFieldCells(snapshot.FieldCells.Span, new int[StateCapacity.MaxRows]);
        }
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverSessionLever(WorldSessionLever lever) { }
    }
}
