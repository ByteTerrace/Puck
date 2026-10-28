using Puck.Maths;
using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class InputOwnerTests {
    [Fact]
    public void SourceCapacityIsIndependentOfTheLogicalActionVocabulary() {
        var catalog = WorldInputCatalog.Compile(declarations: [new("held", ChannelShape.Binary, Composition: true)], modes: null);
        var sources = Enumerable.Range(count: (WorldInputCatalog.MaximumLogicalChannels + 1), start: 0)
            .Select(selector: index => new WorldInputSource(((ushort)index), 0, WorldInputContributionKind.Level, $"source:{index}")).ToArray();
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128);
        var last = sources[^1].Handle;

        Assert.True(condition: producer.Observe(last, FixedQ4816.One.Value, 1, default, 1));
        var batch = producer.Publish(1) with {
            Contributions = sources.Select(selector: source => new WorldInputContribution(source.Handle, source.Channel, source.Kind,
                ((source.Handle == last) ? FixedQ4816.One.Value : 0), ((source.Handle == last) ? 1UL : 0UL),
                NeutralObserved: (source.Handle != last))).ToArray(),
        };
        var writer = new WireWriter();

        Assert.True(condition: WorldWireCodec.TryWriteInputBatch(batch: batch, writer: writer));
        var reader = new WireReader(bytes: writer.ToArray());
        var decoded = WorldWireCodec.ReadInputBatch(maximumEvents: 128, maximumSources: WorldInputCatalog.MaximumSources, reader: ref reader);

        Assert.True(condition: reader.TryFinish(failure: out var wireReason), userMessage: wireReason.ToString());
        Assert.Equal(sources.Length, decoded.Contributions.Length);
        Assert.True(condition: owner.TryAdmit(decoded, new(1, 0, 1680), [], out var reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: 0, hold: out _));
    }
    [Fact]
    public void AForbiddenEngagementRefusesBeforeChangingRoutingOrHeldOrigin() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("routeHold", ChannelShape.Binary, Composition: true)],
            InputModes = new([.. basis.Channels.Select(selector: row => row.Name), "routeHold"], [
                new("engagement", new(WorldInputModeSelectorKind.Engagement), "none", [
                    new("none", [], ["none"]), new("screen", [], ["screen", "machine", "body", "tabletop"]),
                ]),
            ], Combinations: [new Dictionary<string, string> { ["engagement"] = "none" }]),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.JoinSeat();
        fixture.Step();
        var principal = Principal.Seat(slot: 0);
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var channel, name: "routeHold"));
        WorldInputSource[] sources = [new(0, channel, WorldInputContributionKind.Level, "key:hold")];
        var owner = fixture.Server.Input.Admit(bodyIndex: 0, principal: principal, sources: sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        Assert.True(condition: fixture.Server.Input.TryAdmit(principal, producer.Publish(tick), out var reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: channel, hold: out var original));
        var epoch = owner.Epoch;

        Assert.False(condition: fixture.Server.Engagement.Compose(0, GrantSubject.Screen(index: Fixtures.TestPatternScreenIndex), true, principal, principal));
        Assert.Equal(ControlApplication.OwnBody(bodyIndex: 0), Assert.Single(collection: fixture.Server.Grants.Applications(principal: principal)));
        Assert.Equal(epoch, owner.Epoch);
        Assert.True(condition: owner.TryHeld(channel: channel, hold: out var kept));
        Assert.Equal(actual: kept, expected: original);
    }
    [Fact]
    public void LiveReloadKeepsHeldOriginAndRefusesAForbiddenCurrentTupleBeforeInstall() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("reloadHold", ChannelShape.Binary, Composition: true)],
            SeatModesRaw = [new(Name: "focus", DefaultState: "a", States: [new("a"), new("b")])],
            InputModes = new([.. basis.Channels.Select(selector: row => row.Name), "reloadHold"], [
                new("focus", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "focus"), "a", [new("a", [], ["a"]), new("b", [], ["b"])]),
            ]),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.JoinSeat();
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var channel, name: "reloadHold"));
        WorldInputSource[] sources = [new(0, channel, WorldInputContributionKind.Level, "key:hold")];
        var owner = fixture.Server.Input.Admit(Principal.Seat(slot: 0), 0, sources);

        Assert.True(condition: fixture.Server.Input.TrySetSeatState(Principal.Seat(slot: 0), "focus", "b", 0, out var reason), userMessage: reason);
        fixture.Step();
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        Assert.True(condition: fixture.Server.Input.TryAdmit(Principal.Seat(slot: 0), producer.Publish(tick), out reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: channel, hold: out var original));
        var next = definition with { ChannelsRaw = [new("added", ChannelShape.Binary, Composition: true), .. definition.Channels] };

        Reload(replacement: next);
        Assert.True(condition: fixture.Server.Input.TryGet(Principal.Seat(slot: 0), out var after));
        Assert.True(condition: fixture.Server.Population.InputCatalog.TryGetLogicalId(id: out var moved, name: "reloadHold"));
        Assert.True(condition: after!.TryHeld(channel: moved, hold: out var kept));
        Assert.Equal(original.Press, kept.Press);
        Assert.Equal(original.Origin.Event.Offset, kept.Origin.Event.Offset);
        Assert.Equal(original.Origin.Capture, kept.Origin.Capture);
        Assert.Contains("b", after.Mapping.Name);
        var epoch = after.Epoch;
        var forbidden = next with {
            InputModes = next.InputModes! with { Combinations = [new Dictionary<string, string> { ["focus"] = "a" }] },
        };

        Reload(replacement: forbidden);
        Assert.Null(@object: fixture.Server.Definition.InputModes!.Combinations);
        Assert.Equal(epoch, after.Epoch);
        Assert.True(condition: after.TryHeld(channel: moved, hold: out _));

        void Reload(WorldDefinition replacement) {
            fixture.Server.EnqueueRebuild(new WorldRebuildRequest(Kind: WorldRebuildKind.Reload,
                Origin: new WorldRebuildOrigin.File(Path: "input-reload.world.json"), Definition: replacement, Force: true,
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: replacement))), Principal.Console);
            fixture.Step();
        }
    }
    [Fact]
    public void ReloadPreservesAnUnchangedHoldWhileRenumberingAndCancelsChangedMeaning() {
        var oldDefinition = Fixtures.BuildDocument() with {
            ChannelsRaw = [
            new("charge", ChannelShape.Binary, Composition: true), new("note", ChannelShape.Binary, Composition: true),
            new("rest", ChannelShape.Binary, Composition: true), new("sibling", ChannelShape.Binary, Composition: true),
        ],
        };
        var catalog = WorldInputCatalog.For(definition: oldDefinition);
        WorldInputSource[] sources = [new(0, 0, WorldInputContributionKind.Level, "key:charge"), new(1, 1, WorldInputContributionKind.Level, "key:note"),
            new(2, 2, WorldInputContributionKind.Level, "key:rest"), new(3, 3, WorldInputContributionKind.Level, "key:note")];
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128);

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(condition: producer.Observe(1, FixedQ4816.One.Value, 1, FixedQ4816.FromRawBits(value: 1), 2));
        Assert.True(condition: owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: 0, hold: out var charge));
        Assert.True(condition: owner.TryHeld(channel: 1, hold: out var note));
        var definition = oldDefinition with {
            ChannelsRaw = [
            new("added", ChannelShape.Binary, Composition: true), oldDefinition.Channels[0], oldDefinition.Channels[1] with { Threshold = 0.75f },
            oldDefinition.Channels[2] with { Threshold = 0.75f }, oldDefinition.Channels[3],
        ],
        };
        var current = WorldInputCatalog.For(definition: definition);
        WorldInputSource[] replacement = [sources[0] with { Channel = 1 }, sources[1] with { Channel = 2 },
            sources[2] with { Channel = 3 }, sources[3] with { Channel = 4 }];
        var reloaded = new WorldInputOwner(current, current.DefaultMapping, new(), replacement, revision: 2);

        reloaded.InheritDefinition(owner, oldDefinition, definition, new(2, 1680, 1680));
        Assert.True(condition: reloaded.TryHeld(channel: 1, hold: out var continued));
        Assert.Equal(charge.Press, continued.Press);
        Assert.Equal(charge.Origin.Capture, continued.Origin.Capture);
        Assert.Equal(charge.Origin.Event.Offset, continued.Origin.Event.Offset);
        Assert.False(condition: reloaded.TryHeld(channel: 2, hold: out _));
        Assert.True(condition: reloaded.Sources[1].AwaitingNeutral);
        Assert.False(condition: reloaded.Sources[2].AwaitingNeutral);
        Assert.True(condition: reloaded.Sources[3].AwaitingNeutral);
        Assert.True(condition: reloaded.TryEvent(channel: 2, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var cancelled));
        Assert.Equal(note.Press, cancelled.HoldPress);
        Assert.False(condition: reloaded.TryEvent(channel: 1, kind: WorldInputEventKind.Press, occurrence: 0, record: out _));
        var restored = new WorldInputOwner(current, current.DefaultMapping, new(), replacement, revision: 2);

        restored.Restore(checkpoint: reloaded.Capture());
        Assert.True(condition: restored.TryHeld(channel: 1, hold: out var saved));
        Assert.Equal(actual: saved, expected: continued);
    }
    [Fact]
    public void ChangedProfileKeepsUnchangedSourceAndHoldAcrossHandleRenumbering() {
        var catalog = WorldInputCatalog.Compile(declarations: [
            new("charge", ChannelShape.Binary, Composition: true), new("note", ChannelShape.Binary, Composition: true),
        ], modes: null);
        WorldInputSource[] original = [new(Binding: "charge", Channel: 0, Handle: 0, Kind: WorldInputContributionKind.Level, Name: "key:left"), new(Binding: "note", Channel: 1, Handle: 1, Kind: WorldInputContributionKind.Level, Name: "key:right")];
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), original);
        var producer = new WorldInputProducer(catalog, owner.Mapping, original, 128);

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(condition: producer.Observe(1, FixedQ4816.One.Value, 1, FixedQ4816.FromRawBits(value: 1), 2));
        Assert.True(condition: owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: 0, hold: out var charge));
        Assert.True(condition: owner.TryHeld(channel: 1, hold: out var note));
        WorldInputSource[] replacement = [original[1] with { Handle = 0, Binding = "different scale" }, original[0] with { Handle = 1 }];
        var rebound = new WorldInputOwner(catalog, owner.Mapping, new(), replacement, revision: 2);

        rebound.InheritProfile(owner, new(2, 1680, 1680));
        Assert.True(condition: rebound.TryHeld(channel: 0, hold: out var kept));
        Assert.Equal(actual: kept, expected: charge);
        Assert.False(condition: rebound.TryHeld(channel: 1, hold: out _));
        Assert.True(condition: rebound.Sources[0].AwaitingNeutral);
        Assert.Equal(FixedQ4816.One.Value, rebound.Sources[1].RawValue);
        Assert.True(condition: rebound.TryEvent(channel: 1, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var cancelled));
        Assert.Equal(note.Press, cancelled.HoldPress);
        Assert.False(condition: rebound.TryEvent(channel: 0, kind: WorldInputEventKind.Press, occurrence: 0, record: out _));
    }
    [Fact]
    public void ReleaseDiscardedWithAnOldEpochClosesTheKeptHoldOnceInTheNewEpoch() {
        var (owner, catalog) = Create();
        var producer = new WorldInputProducer(catalog, owner.Mapping, [new(0, 0, WorldInputContributionKind.Level, "test:0")], 256);

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(condition: owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: 0, hold: out var original));
        Assert.True(condition: producer.Observe(0, 0, 2, default, 1));
        var discarded = producer.Publish(2);

        owner.Switch(catalog.Mappings[1], new(1, 0, 1680));
        Assert.False(condition: owner.TryAdmit(discarded, new(2, 1680, 1680), [], out _));
        producer.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        var current = producer.Publish(2);

        Assert.Single(collection: current.Events.ToArray());
        Assert.Equal(WorldInputEventKind.Release, current.Events.Span[0].Kind);
        Assert.True(condition: owner.TryAdmit(current, new(2, 1680, 1680), [], out reason), userMessage: reason);
        Assert.False(condition: owner.TryHeld(channel: 0, hold: out _));
        Assert.Equal(original.Press, owner.Record(index: (owner.Count - 1)).HoldPress);
        Assert.Empty(collection: producer.Publish(3).Events.ToArray());
    }
    [Fact]
    public void RoundedCaptureEndpointDefersItsImageAndOrderedEventsUntilTheNextInterval() {
        var (owner, catalog) = Create();
        var producer = new WorldInputProducer(catalog, owner.Mapping, [new(0, 0, WorldInputContributionKind.Level, "test:0")], 256);
        var interval = new WorldInputInterval(1, 0, 1680);
        var stamp = interval.Quantize(denominator: 1_000_000_000, numerator: 33_333_332);

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, stamp.Tick, stamp.Offset, 1, processingTick: 1));
        Assert.True(condition: producer.Text(1, "x", stamp.Tick, stamp.Offset, 1, processingTick: 1));
        var before = producer.Publish(1);

        Assert.Empty(collection: before.Events.ToArray());
        Assert.True(condition: owner.TryAdmit(before, interval, [], out var reason), userMessage: reason);
        var next = producer.Publish(2);

        Assert.Equal(new[] { WorldInputEventKind.Press, WorldInputEventKind.Text }, next.Events.ToArray().Select(selector: row => row.Kind));
        Assert.True(condition: owner.TryAdmit(next, new(2, 1680, 1680), [], out reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: 0, hold: out var hold));
        Assert.Equal(2UL, hold.Origin.Capture.Tick);
    }
    [Fact]
    public void CaptureLossTravelsThroughTheSharedWireLeafAndResetsInsteadOfPublishingTruncatedText() {
        var (owner, catalog) = Create();
        var producer = new WorldInputProducer(catalog, owner.Mapping, [new(0, 0, WorldInputContributionKind.Level, "test:0")], 1);

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(condition: owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), userMessage: reason);
        Assert.False(condition: producer.Text(1, "paste", 2, default));
        var writer = new WireWriter();

        Assert.True(condition: WorldWireCodec.TryWriteInputBatch(writer, producer.Publish(2)));
        var reader = new WireReader(bytes: writer.ToArray());
        var batch = WorldWireCodec.ReadInputBatch(maximumEvents: 1, maximumSources: 1, reader: ref reader);

        Assert.True(condition: reader.TryFinish(failure: out _));
        Assert.True(condition: batch.CaptureLost);
        Assert.False(condition: owner.TryAdmit(batch, new(2, 1680, 1680), [], out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "capture loss");
        Assert.False(condition: owner.TryHeld(channel: 0, hold: out _));
        Assert.Equal(2UL, owner.Epoch);
        producer.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        Assert.False(condition: producer.CaptureLost);
        Assert.False(condition: producer.Observe(0, FixedQ4816.One.Value, 3, default, 1));
    }
    [Fact]
    public void RecompiledProducerKeepsAnObservedReleaseWithoutReplayingItsOldEpochEvent() {
        var catalog = WorldInputCatalog.Compile(declarations: [new("held", ChannelShape.Binary, Composition: true)], modes: null);
        WorldInputSource[] sources = [new(0, 0, WorldInputContributionKind.Level, "key:held")];
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128);

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(condition: owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), userMessage: reason);
        owner.Switch(owner.Mapping, new(2, 1680, 1680), reset: true);
        Assert.True(condition: producer.Observe(0, 0, 2, default, 2));
        var rebuilt = new WorldInputProducer(catalog, owner.Mapping, sources, 128);

        rebuilt.InheritObservations(previous: producer);
        rebuilt.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        Assert.True(condition: rebuilt.Observe(0, FixedQ4816.One.Value, 3, default, 3));
        var batch = rebuilt.Publish(3);

        Assert.True(condition: Assert.Single(collection: batch.Contributions.ToArray()).NeutralObserved);
        Assert.Equal(WorldInputEventKind.Press, Assert.Single(collection: batch.Events.ToArray()).Kind);
        Assert.True(condition: owner.TryAdmit(batch, new(3, 3360, 1680), [], out reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: 0, hold: out var held));
        Assert.Equal(3UL, held.Press);
        var replacement = new WorldInputOwner(catalog, owner.Mapping, new(), sources, revision: 2);

        replacement.RequireNeutralSources(previous: owner);
        Assert.True(condition: (replacement.Epoch > owner.Epoch));
        var fresh = WorldInputProducer.FromAccepted(catalog, replacement.Mapping, replacement.Sources, 128,
            replacement.Revision, replacement.Epoch, replacement.Sequence, replacement.Image);

        Assert.False(condition: fresh.Observe(0, FixedQ4816.One.Value, 4, default, 4));
        Assert.True(condition: fresh.Observe(0, 0, 4, default, 4));
        Assert.True(condition: fresh.Observe(0, FixedQ4816.One.Value, 4, default, 5));
        Assert.True(condition: replacement.TryAdmit(fresh.Publish(4), new(4, 5040, 1680), [], out reason), userMessage: reason);
        Assert.True(condition: replacement.TryHeld(channel: 0, hold: out var next));
        Assert.True(condition: (next.Press > held.Press));
    }
    [Fact]
    public void ProducerPreservesTransientEdgesPasteAndImmutableHeldBatches() {
        var (owner, catalog) = Create();
        var producer = new WorldInputProducer(catalog, owner.Mapping, [new(0, 0, WorldInputContributionKind.Level, "test:0")], 256);

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, 1, FixedQ4816.FromRawBits(value: 1), 1));
        Assert.True(condition: producer.Observe(0, 0, 1, FixedQ4816.FromRawBits(value: 2), 1));
        Assert.True(condition: producer.Text(1, "a🙂", 1, FixedQ4816.FromRawBits(value: 3)));
        var first = producer.Publish(1);

        Assert.True(condition: owner.TryAdmit(first, new(1, 0, 1680), [], out var reason), userMessage: reason);
        Assert.Equal(new[] { WorldInputEventKind.Press, WorldInputEventKind.Release, WorldInputEventKind.Text, WorldInputEventKind.Text }, first.Events.ToArray().Select(selector: row => row.Kind));
        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, 2, default, 2));
        var held = producer.Publish(2);

        Assert.True(condition: owner.TryAdmit(held, new(2, 1680, 1680), [], out reason), userMessage: reason);
        _ = producer.Publish(3);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (ulong tick = 4; (tick < 1004); tick++) { _ = producer.Publish(tick); }
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before));
        Assert.True(condition: first.Contributions.IsEmpty);
        Assert.Equal(FixedQ4816.One.Value, held.Contributions.Span[0].RawValue);
        owner.Switch(catalog.Mappings[1], new(1004, (1680 * 1003), 1680), reset: true);
        producer.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        Assert.False(condition: producer.Observe(0, FixedQ4816.One.Value, 1005, default, 2));
        Assert.True(condition: producer.Observe(0, 0, 1005, default, 2));
        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, 1005, FixedQ4816.FromRawBits(value: 1), 3));
        Assert.True(condition: owner.TryAdmit(producer.Publish(1005), new(1005, (1680 * 1004), 1680), [], out reason), userMessage: reason);
    }
    [Fact]
    public void BinaryWeightsThresholdAfterTheirCombinedSourceFold() {
        var catalog = WorldInputCatalog.Compile(declarations: [new("button", ChannelShape.Binary, Composition: true)], modes: null);
        WorldInputSource[] sources = [new(0, 0, WorldInputContributionKind.Level, "test:0"), new(1, 0, WorldInputContributionKind.Level, "test:1")];
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), sources);
        var producer = new WorldInputProducer(catalog, catalog.DefaultMapping, sources, 128);
        var weight = FixedQ4816.FromRawBits(value: 20_000).Value;

        Assert.True(condition: producer.Observe(0, weight, 1, default, 1));
        Assert.True(condition: producer.Observe(1, weight, 1, default, 2));
        var batch = producer.Publish(1);

        Assert.Single(collection: batch.Events.ToArray());
        Assert.True(condition: owner.TryAdmit(batch, new(1, 0, 1680), [], out var reason), userMessage: reason);
        Assert.Equal(FixedQ4816.One, owner.Image[owner.Mapping.Address(logicalId: 0)]);
    }
    [InlineData(30)]
    [InlineData(60)]
    [Theory]
    public void HistoricalClockJudgesHalfMillisecondBoundaryAtEitherRate(int rate) {
        var width = (50400UL / ((ulong)rate));
        var segment = new WorldInputMusicSegment(ElapsedTicks: (50400 - width), Segment: "phrase", TicksPerBeat: 50400);
        var rawWidth = FixedTickConversion.SecondsOf(ratePerSecond: FixedTickConversion.TicksPerSecond, ticks: width).Value;

        Assert.Equal(-33, segment.PhaseError(FixedQ4816.FromRawBits(value: (rawWidth - 33)), default).Value);
        Assert.Equal(-32, segment.PhaseError(FixedQ4816.FromRawBits(value: (rawWidth - 32)), default).Value);
    }
    [Fact]
    public void ExactIntervalAdmissionPrecedesQuantizationAndRoundedEndpointsCarry() {
        var thirty = new WorldInputInterval(4, 5040, 1680);

        Assert.Equal((5UL, FixedQ4816.Zero), thirty.Quantize(denominator: 1_000_000_000, numerator: 33_333_332));
        var sixty = new WorldInputInterval(4, 2520, 840);

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => sixty.Quantize(denominator: 1_000_000_000, numerator: 16_666_768));
    }
    [Fact]
    public void DelayedInputCannotRestoreReleasedActionsAfterAnEpochBarrier() {
        var basis = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(definition: basis with {
            ChannelsRaw = [.. basis.Channels, new("held", ChannelShape.Binary, Composition: true)],
        });

        fixture.JoinSeat();
        fixture.Step();
        var population = fixture.Server.Population;
        var body = fixture.Server.Body(index: 0)!;

        Assert.True(condition: body.Channels.TryGetOrdinal(name: "held", ordinal: out var button));
        var forward = body.Channels.RoleOrdinals.MoveAdvance;
        var delay = new WorldInputHoldRuntime(new(CeilingTicks: 3, DefaultTicks: 2, EqualizeByDefault: false, LowerAfterTicks: 1, Participants: []), population.Capacity);

        delay.PrepareParticipants(population: population);
        for (var tick = 0; (tick < 4); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: forward, value: FixedQ4816.One));
            body.SetHeldChannels(channels: default(PlayerIntent).WithChannel(ordinal: button, value: FixedQ4816.One));
            delay.Apply(population: population);
        }
        var kept = default(PlayerIntent).WithChannel(ordinal: forward, value: FixedQ4816.One);

        delay.InstallBarrier(0, kept, body.Channels);
        var restored = new WorldInputHoldRuntime(new(CeilingTicks: 3, DefaultTicks: 2, EqualizeByDefault: false, LowerAfterTicks: 1, Participants: []), population.Capacity);

        restored.Restore(checkpoint: delay.Capture());
        body.SubmitIntent(intent: default);
        body.SetHeldChannels(channels: default);
        restored.Apply(population: population);
        var sample = body.TakeSubmittedInput();

        Assert.Equal(FixedQ4816.One, sample.Intent[forward]);
        Assert.Equal(FixedQ4816.Zero, sample.HeldChannels[button]);
        restored.InstallBarrier(0, default, body.Channels);
        restored.Apply(population: population);
        sample = body.TakeSubmittedInput();
        Assert.Equal(FixedQ4816.Zero, sample.Intent[forward]);
        Assert.Equal(FixedQ4816.Zero, sample.HeldChannels[button]);
    }
    [Fact]
    public void MidHoldAuthorityCheckpointAndWireRetainOriginAndHash() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("held", ChannelShape.Binary, Composition: true)],
            InputHistory = new(CalibrationLimitSeconds: 0.02m),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.JoinSeat();
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var channel, name: "held"));
        var owner = fixture.Server.Input.Admit(Principal.Seat(slot: 0), 0, [new(0, channel, WorldInputContributionKind.Level, "test:0")]);
        var interval = fixture.Server.Input.Interval;
        var batch = new WorldInputBatch(owner.Revision, owner.Epoch, owner.Mapping.Id, interval.Tick,
            default(PlayerIntent).WithChannel(ordinal: owner.Mapping.Address(logicalId: channel), value: FixedQ4816.One),
            new WorldInputContribution[] { new(0, channel, WorldInputContributionKind.Level, FixedQ4816.One.Value, 1) },
            new WorldInputEvent[] { new(channel, WorldInputEventKind.Press, FixedQ4816.One.Value, interval.Tick, FixedQ4816.FromRawBits(value: 7), 1, 1) }, owner.Mapping.NumericCount);
        var writer = new WireWriter();

        Assert.True(condition: WorldWireCodec.TryWriteInputBatch(batch: batch, writer: writer));
        var reader = new WireReader(bytes: writer.ToArray());
        var decodedBatch = WorldWireCodec.ReadInputBatch(maximumEvents: 128, maximumSources: 1, reader: ref reader);

        Assert.True(condition: reader.TryFinish(failure: out var wireReason), userMessage: wireReason.ToString());
        var admission = fixture.Server.ApplyIntentSubmission(body: fixture.Server.Body(index: 0)!, submission: new(interval.Tick, 0, default, Principal.Seat(slot: 0), Input: decodedBatch));

        Assert.True(condition: admission.IsAccepted, userMessage: admission.InputRefusal);
        var stale = fixture.Server.ApplyIntentSubmission(body: fixture.Server.Body(index: 0)!, submission: new(interval.Tick, 0, default, Principal.Seat(slot: 0), Input: decodedBatch with { Epoch = (decodedBatch.Epoch + 1) }));

        Assert.True(condition: stale.Permission.IsAllowed);
        Assert.False(condition: stale.IsAccepted);
        Assert.Contains("stale", stale.InputRefusal);
        Assert.True(condition: owner.TryHeld(channel: channel, hold: out var held));
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var captured, out var reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!), out var decoded, out reason), userMessage: reason);
        using var restored = Fixtures.FreshServer(definition: definition);

        restored.Server.RestoreCheckpoint(checkpoint: decoded!);
        Assert.True(condition: restored.Server.Input.TryGet(Principal.Seat(slot: 0), out var restoredOwner));
        Assert.True(condition: restoredOwner!.TryHeld(channel: channel, hold: out var restoredHold));
        Assert.Equal(actual: restoredHold, expected: held);
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: interval.Tick),
            WorldStateHashComposition.HashAuthoritative(server: restored.Server, tick: interval.Tick));
        WorldInputSource[] sources = [new(0, channel, WorldInputContributionKind.Level, "test:0")];
        var epoch = restoredOwner.Epoch;

        restored.Server.Input.Reconfigure(definition: definition with { ChannelsRaw = definition.Channels.ToArray() });
        Assert.Equal(epoch, restoredOwner.Epoch);
        Assert.True(condition: restoredOwner.TryHeld(channel: channel, hold: out _));
        Assert.True(condition: restored.Server.Input.TryAdmitProfile(Principal.Seat(slot: 0), 0, sources, out reason), userMessage: reason);
        Assert.Equal(epoch, restoredOwner.Epoch);
        Assert.True(condition: restoredOwner.TryHeld(channel: channel, hold: out _));
        Assert.True(condition: restored.Server.Input.TryAdmitProfile(Principal.Seat(slot: 0), 0, sources, out reason, freshAttachment: true), userMessage: reason);
        Assert.True(condition: (restoredOwner.Epoch > epoch));
        Assert.False(condition: restoredOwner.TryHeld(channel: channel, hold: out _));
        Assert.True(condition: restoredOwner.Sources[0].AwaitingNeutral);
        Assert.True(condition: restoredOwner.TryEvent(channel: channel, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var cancellation));
        Assert.Equal(held.Press, cancellation.HoldPress);
        Assert.True(condition: restored.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var cancelledCheckpoint, out reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint: cancelledCheckpoint!), out var cancelledDecoded, out reason), userMessage: reason);
        using var cancelledRestore = Fixtures.FreshServer(definition: definition);

        cancelledRestore.Server.RestoreCheckpoint(checkpoint: cancelledDecoded!);
        Assert.True(condition: cancelledRestore.Server.Input.TryGet(Principal.Seat(slot: 0), out var cancelledOwner));
        Assert.True(condition: cancelledOwner!.TryEvent(channel: channel, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var savedCancellation));
        Assert.Equal(actual: savedCancellation, expected: cancellation);
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(server: restored.Server, tick: interval.Tick),
            WorldStateHashComposition.HashAuthoritative(server: cancelledRestore.Server, tick: interval.Tick));
        var cancelledEpoch = cancelledOwner.Epoch;

        Assert.False(condition: cancelledRestore.Server.Input.TryCalibrate(Principal.Seat(slot: 0), FixedQ4816.One, out reason));
        Assert.Equal(cancelledEpoch, cancelledOwner.Epoch);
        Assert.True(condition: cancelledRestore.Server.Input.TryCalibrate(Principal.Seat(slot: 0), FixedQ4816.FromRawBits(value: 32), out reason), userMessage: reason);
        Assert.Equal((cancelledEpoch + 1), cancelledOwner.Epoch);
        Assert.Equal(32, cancelledOwner.Calibration.Value);
        var grant = new WorldGrant(Principal.Seat(slot: 0), WorldCapability.Drive, GrantSubject.Body(index: 0), false);

        fixture.Server.Revoke(grant, Principal.Console);
        Assert.False(condition: owner.TryHeld(channel: channel, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: channel, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var revoked));
        Assert.Equal(held.Press, revoked.HoldPress);
        fixture.Server.Grant(grant, Principal.Console);
        Assert.False(condition: owner.TryHeld(channel: channel, hold: out _));
        Assert.True(condition: owner.Sources[0].AwaitingNeutral);
    }

    private static (WorldInputOwner Owner, WorldInputCatalog Catalog) Create(int capacity = 256) {
        var catalog = WorldInputCatalog.Compile(declarations: [
            new("hold", ChannelShape.Binary, Composition: true),
            new("text", ChannelShape.Text, Composition: true),
        ], modes: new WorldInputModes(["hold", "text"], [new("focus", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "focus"), "a",
            [new("a", []), new("b", [])], Transitions: [new("a", "b", ["hold"])])]));

        return (new WorldInputOwner(catalog, catalog.Mappings[0], new(EventCapacity: capacity, MaximumEventsPerStep: capacity), [new(0, 0, WorldInputContributionKind.Level, "test:0")]), catalog);
    }
    private static WorldInputEvent Edge(uint sequence, bool press, ulong tick = 1, long offset = 0) =>
        new(0, (press ? WorldInputEventKind.Press : WorldInputEventKind.Release), (press ? FixedQ4816.One.Value : 0), tick, FixedQ4816.FromRawBits(value: offset), sequence, sequence);
    private static WorldInputBatch Batch(WorldInputOwner owner, WorldInputEvent[] events, bool held = false, ulong tick = 1) =>
        new(owner.Revision, owner.Epoch, owner.Mapping.Id, tick, default(PlayerIntent).WithChannel(ordinal: owner.Mapping.Address(logicalId: 0), value: (held ? FixedQ4816.One : FixedQ4816.Zero)),
            new WorldInputContribution[] { new(0, 0, WorldInputContributionKind.Level, (held ? FixedQ4816.One.Value : 0), 1) }, events);

    [Fact]
    public void PressAndReleaseWithinOneTickRetainBothTimesAndOrigin() {
        var (owner, _) = Create();
        var interval = new WorldInputInterval(1, 0, 1680);

        Assert.True(condition: owner.TryAdmit(Batch(owner, [Edge(1, true, offset: 1), Edge(2, false, offset: 2)]), interval, [], out var reason), userMessage: reason);
        Assert.Equal(2, owner.Count);
        Assert.Equal(owner.Record(index: 0).Id, owner.Record(index: 1).HoldPress);
        Assert.True(condition: (owner.Record(index: 0).Time(policy: WorldInputJudgement.Capture) < owner.Record(index: 1).Time(policy: WorldInputJudgement.Capture)));
        Assert.False(condition: owner.TryHeld(channel: 0, hold: out _));
    }
    [Fact]
    public void KeptHoldRetainsOriginWithoutNewEdgeAndReleaseClosesIt() {
        var (owner, catalog) = Create();
        Assert.True(condition: owner.TryAdmit(Batch(owner, [Edge(1, true)], true), new(1, 0, 1680), [], out var reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: 0, hold: out var original));
        owner.Switch(catalog.Mappings[1], new(1, 0, 1680), consumedActivation: 1);
        Assert.True(condition: owner.TryHeld(channel: 0, hold: out var kept));
        Assert.Equal(original.Press, kept.Press);
        Assert.Equal(original.Origin, kept.Origin);
        Assert.True(condition: kept.Consumed);
        Assert.Equal(1, owner.SwitchCount);
        var decision = Assert.Single(collection: owner.SwitchRecord(index: 0).Decisions.ToArray());

        Assert.Equal(WorldInputContinuationReason.Kept, decision.Reason);
        Assert.Equal(original.Press, decision.HoldPress);
        Assert.False(condition: owner.TryEvent(channel: 0, kind: WorldInputEventKind.Press, occurrence: 0, record: out _));
        Assert.True(condition: owner.TryAdmit(Batch(owner, [Edge(1, false, 2)], tick: 2), new(2, 1680, 1680), [], out reason), userMessage: reason);
        Assert.Equal(original.Press, owner.Record(index: 1).HoldPress);
    }
    [Fact]
    public void ReleasedSourceNeedsExplicitNeutralAndStaleEpochCannotAct() {
        var (owner, catalog) = Create();
        Assert.True(condition: owner.TryAdmit(Batch(owner, [Edge(1, true)], true), new(1, 0, 1680), [], out _));
        var stale = Batch(events: [], held: true, owner: owner, tick: 2);

        owner.Switch(catalog.Mappings[1], new(1, 0, 1680), reset: true);
        Assert.False(condition: owner.TryHeld(channel: 0, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: 0, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var cancellation));
        Assert.Equal(owner.Record(index: 0).Id, cancellation.HoldPress);
        Assert.False(condition: owner.TryEvent(channel: 0, kind: WorldInputEventKind.Release, occurrence: 0, record: out _));
        Assert.False(condition: owner.TryAdmit(stale, new(2, 1680, 1680), [], out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "stale");
        Assert.False(condition: owner.TryAdmit(Batch(owner, [Edge(1, true, 2)], true, 2), new(2, 1680, 1680), [], out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "neutral");
        Assert.True(condition: owner.TryAdmit(Batch(owner, [], tick: 2), new(2, 1680, 1680), [], out reason), userMessage: reason);
        Assert.True(condition: owner.TryAdmit(Batch(owner, [Edge(1, true, 3)], true, 3), new(3, 3360, 1680), [], out reason), userMessage: reason);
    }
    [Fact]
    public void TextPastePreservesEqualTimeOrderAndRefusesNonScalarsAtomically() {
        var (owner, _) = Create();
        WorldInputEvent[] text = [new(Activation: 0, CaptureTick: 1, Channel: 1, Kind: WorldInputEventKind.Text, Offset: default, RawValue: 'h', Sequence: 1), new(Activation: 0, CaptureTick: 1, Channel: 1, Kind: WorldInputEventKind.Text, Offset: default, RawValue: 'i', Sequence: 2), new(Activation: 0, CaptureTick: 1, Channel: 1, Kind: WorldInputEventKind.Text, Offset: default, RawValue: 0x1f642, Sequence: 3)];

        Assert.True(condition: owner.TryAdmit(Batch(owner, text), new(1, 0, 1680), [], out var reason), userMessage: reason);
        Assert.Equal(new long[] { 'h', 'i', 0x1f642 }, Enumerable.Range(0, owner.Count).Select(selector: i => owner.Record(index: i).Event.RawValue));
        Assert.False(condition: owner.TryAdmit(Batch(owner, [new(Activation: 0, CaptureTick: 2, Channel: 1, Kind: WorldInputEventKind.Text, Offset: default, RawValue: 0xd800, Sequence: 4)], tick: 2), new(2, 1680, 1680), [], out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "Unicode scalar");
        Assert.Equal(3, owner.Count);
    }
    [Fact]
    public void SteadyHeldAdmissionAllocatesNothing() {
        var (owner, _) = Create();
        Assert.True(condition: owner.TryAdmit(Batch(owner, [Edge(1, true)], true), new(1, 0, 1680), [], out _));
        var batch = Batch(events: [], held: true, owner: owner, tick: 2);

        for (var i = 0; (i < 100); i++) { Assert.True(condition: owner.TryAdmit(batch, new(2, 1680, 1680), [], out _)); }
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; (i < 1000); i++) {
            if (!owner.TryAdmit(batch, new(2, 1680, 1680), [], out _)) { throw new InvalidOperationException(); }
        }
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before));
    }
}
