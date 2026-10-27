using Puck.Maths;
using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class InputOwnerTests {
    [Fact]
    public void SourceCapacityIsIndependentOfTheLogicalActionVocabulary() {
        var catalog = WorldInputCatalog.Compile([new("held", ChannelShape.Binary, Composition: true)], null);
        var sources = Enumerable.Range(0, WorldInputCatalog.MaximumLogicalChannels + 1)
            .Select(index => new WorldInputSource((ushort)index, 0, WorldInputContributionKind.Level, $"source:{index}")).ToArray();
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128);
        var last = sources[^1].Handle;
        Assert.True(producer.Observe(last, FixedQ4816.One.Value, 1, default, 1));
        var batch = producer.Publish(1) with {
            Contributions = sources.Select(source => new WorldInputContribution(source.Handle, source.Channel, source.Kind,
                source.Handle == last ? FixedQ4816.One.Value : 0, source.Handle == last ? 1UL : 0UL,
                NeutralObserved: source.Handle != last)).ToArray(),
        };
        var writer = new WireWriter();
        Assert.True(WorldWireCodec.TryWriteInputBatch(writer, batch));
        var reader = new WireReader(writer.ToArray());
        var decoded = WorldWireCodec.ReadInputBatch(ref reader, WorldInputCatalog.MaximumSources, 128);
        Assert.True(reader.TryFinish(out var wireReason), wireReason.ToString());
        Assert.Equal(sources.Length, decoded.Contributions.Length);
        Assert.True(owner.TryAdmit(decoded, new(1, 0, 1680), [], out var reason), reason);
        Assert.True(owner.TryHeld(0, out _));
    }

    [Fact]
    public void AForbiddenEngagementRefusesBeforeChangingRoutingOrHeldOrigin() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("routeHold", ChannelShape.Binary, Composition: true)],
            InputModes = new([.. basis.Channels.Select(row => row.Name), "routeHold"], [
                new("engagement", new(WorldInputModeSelectorKind.Engagement), "none", [
                    new("none", [], ["none"]), new("screen", [], ["screen", "machine", "body", "tabletop"]),
                ]),
            ], Combinations: [new Dictionary<string, string> { ["engagement"] = "none" }]),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.JoinSeat();
        fixture.Step();
        var principal = Principal.Seat(0);
        var catalog = fixture.Server.Population.InputCatalog;
        Assert.True(catalog.TryGetLogicalId("routeHold", out var channel));
        WorldInputSource[] sources = [new(0, channel, WorldInputContributionKind.Level, "key:hold")];
        var owner = fixture.Server.Input.Admit(principal, 0, sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        Assert.True(fixture.Server.Input.TryAdmit(principal, producer.Publish(tick), out var reason), reason);
        Assert.True(owner.TryHeld(channel, out var original));
        var epoch = owner.Epoch;
        Assert.False(fixture.Server.Engagement.Compose(0, GrantSubject.Screen(Fixtures.TestPatternScreenIndex), true, principal, principal));
        Assert.Equal(ControlApplication.OwnBody(0), Assert.Single(fixture.Server.Grants.Applications(principal)));
        Assert.Equal(epoch, owner.Epoch);
        Assert.True(owner.TryHeld(channel, out var kept));
        Assert.Equal(original, kept);
    }

    [Fact]
    public void LiveReloadKeepsHeldOriginAndRefusesAForbiddenCurrentTupleBeforeInstall() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("reloadHold", ChannelShape.Binary, Composition: true)],
            SeatModesRaw = [new(Name: "focus", DefaultState: "a", States: [new("a"), new("b")])],
            InputModes = new([.. basis.Channels.Select(row => row.Name), "reloadHold"], [
                new("focus", new(WorldInputModeSelectorKind.SeatState, "focus"), "a", [new("a", [], ["a"]), new("b", [], ["b"])]),
            ]),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.JoinSeat();
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;
        Assert.True(catalog.TryGetLogicalId("reloadHold", out var channel));
        WorldInputSource[] sources = [new(0, channel, WorldInputContributionKind.Level, "key:hold")];
        var owner = fixture.Server.Input.Admit(Principal.Seat(0), 0, sources);
        Assert.True(fixture.Server.Input.TrySetSeatState(Principal.Seat(0), "focus", "b", 0, out var reason), reason);
        fixture.Step();
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        Assert.True(fixture.Server.Input.TryAdmit(Principal.Seat(0), producer.Publish(tick), out reason), reason);
        Assert.True(owner.TryHeld(channel, out var original));
        var next = definition with { ChannelsRaw = [new("added", ChannelShape.Binary, Composition: true), .. definition.Channels] };
        Reload(next);
        Assert.True(fixture.Server.Input.TryGet(Principal.Seat(0), out var after));
        Assert.True(fixture.Server.Population.InputCatalog.TryGetLogicalId("reloadHold", out var moved));
        Assert.True(after!.TryHeld(moved, out var kept));
        Assert.Equal(original.Press, kept.Press);
        Assert.Equal(original.Origin.Event.Offset, kept.Origin.Event.Offset);
        Assert.Equal(original.Origin.Capture, kept.Origin.Capture);
        Assert.Contains("b", after.Mapping.Name);
        var epoch = after.Epoch;
        var forbidden = next with {
            InputModes = next.InputModes! with { Combinations = [new Dictionary<string, string> { ["focus"] = "a" }] },
        };
        Reload(forbidden);
        Assert.Null(fixture.Server.Definition.InputModes!.Combinations);
        Assert.Equal(epoch, after.Epoch);
        Assert.True(after.TryHeld(moved, out _));

        void Reload(WorldDefinition replacement) {
            fixture.Server.EnqueueRebuild(new WorldRebuildRequest(Kind: WorldRebuildKind.Reload,
                Origin: new WorldRebuildOrigin.File("input-reload.world.json"), Definition: replacement, Force: true,
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(WorldDefinitionSerialization.Serialize(replacement))), Principal.Console);
            fixture.Step();
        }
    }

    [Fact]
    public void ReloadPreservesAnUnchangedHoldWhileRenumberingAndCancelsChangedMeaning() {
        var oldDefinition = Fixtures.BuildDocument() with { ChannelsRaw = [
            new("charge", ChannelShape.Binary, Composition: true), new("note", ChannelShape.Binary, Composition: true),
            new("rest", ChannelShape.Binary, Composition: true), new("sibling", ChannelShape.Binary, Composition: true),
        ] };
        var catalog = WorldInputCatalog.For(oldDefinition);
        WorldInputSource[] sources = [new(0, 0, WorldInputContributionKind.Level, "key:charge"), new(1, 1, WorldInputContributionKind.Level, "key:note"),
            new(2, 2, WorldInputContributionKind.Level, "key:rest"), new(3, 3, WorldInputContributionKind.Level, "key:note")];
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128);
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(producer.Observe(1, FixedQ4816.One.Value, 1, FixedQ4816.FromRawBits(1), 2));
        Assert.True(owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), reason);
        Assert.True(owner.TryHeld(0, out var charge));
        Assert.True(owner.TryHeld(1, out var note));
        var definition = oldDefinition with { ChannelsRaw = [
            new("added", ChannelShape.Binary, Composition: true), oldDefinition.Channels[0], oldDefinition.Channels[1] with { Threshold = 0.75f },
            oldDefinition.Channels[2] with { Threshold = 0.75f }, oldDefinition.Channels[3],
        ] };
        var current = WorldInputCatalog.For(definition);
        WorldInputSource[] replacement = [sources[0] with { Channel = 1 }, sources[1] with { Channel = 2 },
            sources[2] with { Channel = 3 }, sources[3] with { Channel = 4 }];
        var reloaded = new WorldInputOwner(current, current.DefaultMapping, new(), replacement, revision: 2);
        reloaded.InheritDefinition(owner, oldDefinition, definition, new(2, 1680, 1680));
        Assert.True(reloaded.TryHeld(1, out var continued));
        Assert.Equal(charge.Press, continued.Press);
        Assert.Equal(charge.Origin.Capture, continued.Origin.Capture);
        Assert.Equal(charge.Origin.Event.Offset, continued.Origin.Event.Offset);
        Assert.False(reloaded.TryHeld(2, out _));
        Assert.True(reloaded.Sources[1].AwaitingNeutral);
        Assert.False(reloaded.Sources[2].AwaitingNeutral);
        Assert.True(reloaded.Sources[3].AwaitingNeutral);
        Assert.True(reloaded.TryEvent(2, WorldInputEventKind.Cancel, 0, out var cancelled));
        Assert.Equal(note.Press, cancelled.HoldPress);
        Assert.False(reloaded.TryEvent(1, WorldInputEventKind.Press, 0, out _));
        var restored = new WorldInputOwner(current, current.DefaultMapping, new(), replacement, revision: 2);
        restored.Restore(reloaded.Capture());
        Assert.True(restored.TryHeld(1, out var saved));
        Assert.Equal(continued, saved);
    }

    [Fact]
    public void ChangedProfileKeepsUnchangedSourceAndHoldAcrossHandleRenumbering() {
        var catalog = WorldInputCatalog.Compile([
            new("charge", ChannelShape.Binary, Composition: true), new("note", ChannelShape.Binary, Composition: true),
        ], null);
        WorldInputSource[] original = [new(0, 0, WorldInputContributionKind.Level, "key:left", "charge"), new(1, 1, WorldInputContributionKind.Level, "key:right", "note")];
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), original);
        var producer = new WorldInputProducer(catalog, owner.Mapping, original, 128);
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(producer.Observe(1, FixedQ4816.One.Value, 1, FixedQ4816.FromRawBits(1), 2));
        Assert.True(owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), reason);
        Assert.True(owner.TryHeld(0, out var charge));
        Assert.True(owner.TryHeld(1, out var note));
        WorldInputSource[] replacement = [original[1] with { Handle = 0, Binding = "different scale" }, original[0] with { Handle = 1 }];
        var rebound = new WorldInputOwner(catalog, owner.Mapping, new(), replacement, revision: 2);
        rebound.InheritProfile(owner, new(2, 1680, 1680));
        Assert.True(rebound.TryHeld(0, out var kept));
        Assert.Equal(charge, kept);
        Assert.False(rebound.TryHeld(1, out _));
        Assert.True(rebound.Sources[0].AwaitingNeutral);
        Assert.Equal(FixedQ4816.One.Value, rebound.Sources[1].RawValue);
        Assert.True(rebound.TryEvent(1, WorldInputEventKind.Cancel, 0, out var cancelled));
        Assert.Equal(note.Press, cancelled.HoldPress);
        Assert.False(rebound.TryEvent(0, WorldInputEventKind.Press, 0, out _));
    }

    [Fact]
    public void ReleaseDiscardedWithAnOldEpochClosesTheKeptHoldOnceInTheNewEpoch() {
        var (owner, catalog) = Create();
        var producer = new WorldInputProducer(catalog, owner.Mapping, [new(0, 0, WorldInputContributionKind.Level, "test:0")], 256);
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), reason);
        Assert.True(owner.TryHeld(0, out var original));
        Assert.True(producer.Observe(0, 0, 2, default, 1));
        var discarded = producer.Publish(2);
        owner.Switch(catalog.Mappings[1], new(1, 0, 1680));
        Assert.False(owner.TryAdmit(discarded, new(2, 1680, 1680), [], out _));
        producer.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        var current = producer.Publish(2);
        Assert.Single(current.Events.ToArray());
        Assert.Equal(WorldInputEventKind.Release, current.Events.Span[0].Kind);
        Assert.True(owner.TryAdmit(current, new(2, 1680, 1680), [], out reason), reason);
        Assert.False(owner.TryHeld(0, out _));
        Assert.Equal(original.Press, owner.Record(owner.Count - 1).HoldPress);
        Assert.Empty(producer.Publish(3).Events.ToArray());
    }

    [Fact]
    public void RoundedCaptureEndpointDefersItsImageAndOrderedEventsUntilTheNextInterval() {
        var (owner, catalog) = Create();
        var producer = new WorldInputProducer(catalog, owner.Mapping, [new(0, 0, WorldInputContributionKind.Level, "test:0")], 256);
        var interval = new WorldInputInterval(1, 0, 1680);
        var stamp = interval.Quantize(33_333_332, 1_000_000_000);
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, stamp.Tick, stamp.Offset, 1, processingTick: 1));
        Assert.True(producer.Text(1, "x", stamp.Tick, stamp.Offset, 1, processingTick: 1));
        var before = producer.Publish(1);
        Assert.Empty(before.Events.ToArray());
        Assert.True(owner.TryAdmit(before, interval, [], out var reason), reason);
        var next = producer.Publish(2);
        Assert.Equal(new[] { WorldInputEventKind.Press, WorldInputEventKind.Text }, next.Events.ToArray().Select(row => row.Kind));
        Assert.True(owner.TryAdmit(next, new(2, 1680, 1680), [], out reason), reason);
        Assert.True(owner.TryHeld(0, out var hold));
        Assert.Equal(2UL, hold.Origin.Capture.Tick);
    }

    [Fact]
    public void CaptureLossTravelsThroughTheSharedWireLeafAndResetsInsteadOfPublishingTruncatedText() {
        var (owner, catalog) = Create();
        var producer = new WorldInputProducer(catalog, owner.Mapping, [new(0, 0, WorldInputContributionKind.Level, "test:0")], 1);
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), reason);
        Assert.False(producer.Text(1, "paste", 2, default));
        var writer = new WireWriter();
        Assert.True(WorldWireCodec.TryWriteInputBatch(writer, producer.Publish(2)));
        var reader = new WireReader(writer.ToArray());
        var batch = WorldWireCodec.ReadInputBatch(ref reader, 1, 1);
        Assert.True(reader.TryFinish(out _));
        Assert.True(batch.CaptureLost);
        Assert.False(owner.TryAdmit(batch, new(2, 1680, 1680), [], out reason));
        Assert.Contains("capture loss", reason);
        Assert.False(owner.TryHeld(0, out _));
        Assert.Equal(2UL, owner.Epoch);
        producer.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        Assert.False(producer.CaptureLost);
        Assert.False(producer.Observe(0, FixedQ4816.One.Value, 3, default, 1));
    }

    [Fact]
    public void RecompiledProducerKeepsAnObservedReleaseWithoutReplayingItsOldEpochEvent() {
        var catalog = WorldInputCatalog.Compile([new("held", ChannelShape.Binary, Composition: true)], null);
        WorldInputSource[] sources = [new(0, 0, WorldInputContributionKind.Level, "key:held")];
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128);
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, 1, default, 1));
        Assert.True(owner.TryAdmit(producer.Publish(1), new(1, 0, 1680), [], out var reason), reason);
        owner.Switch(owner.Mapping, new(2, 1680, 1680), reset: true);
        Assert.True(producer.Observe(0, 0, 2, default, 2));
        var rebuilt = new WorldInputProducer(catalog, owner.Mapping, sources, 128);
        rebuilt.InheritObservations(producer);
        rebuilt.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        Assert.True(rebuilt.Observe(0, FixedQ4816.One.Value, 3, default, 3));
        var batch = rebuilt.Publish(3);
        Assert.True(Assert.Single(batch.Contributions.ToArray()).NeutralObserved);
        Assert.Equal(WorldInputEventKind.Press, Assert.Single(batch.Events.ToArray()).Kind);
        Assert.True(owner.TryAdmit(batch, new(3, 3360, 1680), [], out reason), reason);
        Assert.True(owner.TryHeld(0, out var held));
        Assert.Equal(3UL, held.Press);
        var replacement = new WorldInputOwner(catalog, owner.Mapping, new(), sources, revision: 2);
        replacement.RequireNeutralSources(owner);
        Assert.True(replacement.Epoch > owner.Epoch);
        var fresh = WorldInputProducer.FromAccepted(catalog, replacement.Mapping, replacement.Sources, 128,
            replacement.Revision, replacement.Epoch, replacement.Sequence, replacement.Image);
        Assert.False(fresh.Observe(0, FixedQ4816.One.Value, 4, default, 4));
        Assert.True(fresh.Observe(0, 0, 4, default, 4));
        Assert.True(fresh.Observe(0, FixedQ4816.One.Value, 4, default, 5));
        Assert.True(replacement.TryAdmit(fresh.Publish(4), new(4, 5040, 1680), [], out reason), reason);
        Assert.True(replacement.TryHeld(0, out var next));
        Assert.True(next.Press > held.Press);
    }

    [Fact]
    public void ProducerPreservesTransientEdgesPasteAndImmutableHeldBatches() {
        var (owner, catalog) = Create();
        var producer = new WorldInputProducer(catalog, owner.Mapping, [new(0, 0, WorldInputContributionKind.Level, "test:0")], 256);
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, 1, FixedQ4816.FromRawBits(1), 1));
        Assert.True(producer.Observe(0, 0, 1, FixedQ4816.FromRawBits(2), 1));
        Assert.True(producer.Text(1, "a🙂", 1, FixedQ4816.FromRawBits(3)));
        var first = producer.Publish(1);
        Assert.True(owner.TryAdmit(first, new(1, 0, 1680), [], out var reason), reason);
        Assert.Equal(new[] { WorldInputEventKind.Press, WorldInputEventKind.Release, WorldInputEventKind.Text, WorldInputEventKind.Text }, first.Events.ToArray().Select(row => row.Kind));
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, 2, default, 2));
        var held = producer.Publish(2);
        Assert.True(owner.TryAdmit(held, new(2, 1680, 1680), [], out reason), reason);
        _ = producer.Publish(3);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong tick = 4; tick < 1004; tick++) { _ = producer.Publish(tick); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(first.Contributions.IsEmpty);
        Assert.Equal(FixedQ4816.One.Value, held.Contributions.Span[0].RawValue);
        owner.Switch(catalog.Mappings[1], new(1004, 1680 * 1003, 1680), reset: true);
        producer.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        Assert.False(producer.Observe(0, FixedQ4816.One.Value, 1005, default, 2));
        Assert.True(producer.Observe(0, 0, 1005, default, 2));
        Assert.True(producer.Observe(0, FixedQ4816.One.Value, 1005, FixedQ4816.FromRawBits(1), 3));
        Assert.True(owner.TryAdmit(producer.Publish(1005), new(1005, 1680 * 1004, 1680), [], out reason), reason);
    }

    [Fact]
    public void BinaryWeightsThresholdAfterTheirCombinedSourceFold() {
        var catalog = WorldInputCatalog.Compile([new("button", ChannelShape.Binary, Composition: true)], null);
        WorldInputSource[] sources = [new(0, 0, WorldInputContributionKind.Level, "test:0"), new(1, 0, WorldInputContributionKind.Level, "test:1")];
        var owner = new WorldInputOwner(catalog, catalog.DefaultMapping, new(), sources);
        var producer = new WorldInputProducer(catalog, catalog.DefaultMapping, sources, 128);
        var weight = FixedQ4816.FromRawBits(20_000).Value;
        Assert.True(producer.Observe(0, weight, 1, default, 1));
        Assert.True(producer.Observe(1, weight, 1, default, 2));
        var batch = producer.Publish(1);
        Assert.Single(batch.Events.ToArray());
        Assert.True(owner.TryAdmit(batch, new(1, 0, 1680), [], out var reason), reason);
        Assert.Equal(FixedQ4816.One, owner.Image[owner.Mapping.Address(0)]);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void HistoricalClockJudgesHalfMillisecondBoundaryAtEitherRate(int rate) {
        var width = 50400UL / (ulong)rate;
        var segment = new WorldInputMusicSegment(50400 - width, 50400, "phrase");
        var rawWidth = FixedTickConversion.SecondsOf(width, FixedTickConversion.TicksPerSecond).Value;
        Assert.Equal(-33, segment.PhaseError(FixedQ4816.FromRawBits(rawWidth - 33), default).Value);
        Assert.Equal(-32, segment.PhaseError(FixedQ4816.FromRawBits(rawWidth - 32), default).Value);
    }

    [Fact]
    public void ExactIntervalAdmissionPrecedesQuantizationAndRoundedEndpointsCarry() {
        var thirty = new WorldInputInterval(4, 5040, 1680);
        Assert.Equal((5UL, FixedQ4816.Zero), thirty.Quantize(33_333_332, 1_000_000_000));
        var sixty = new WorldInputInterval(4, 2520, 840);
        Assert.Throws<ArgumentOutOfRangeException>(() => sixty.Quantize(16_666_768, 1_000_000_000));
    }
    [Fact]
    public void DelayedInputCannotRestoreReleasedActionsAfterAnEpochBarrier() {
        var basis = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(definition: basis with {
            ChannelsRaw = [.. basis.Channels, new("held", ChannelShape.Binary, Composition: true)]
        });
        fixture.JoinSeat();
        fixture.Step();
        var population = fixture.Server.Population;
        var body = fixture.Server.Body(0)!;
        Assert.True(body.Channels.TryGetOrdinal("held", out var button));
        var forward = body.Channels.RoleOrdinals.MoveAdvance;
        var delay = new WorldInputHoldRuntime(new(3, 1, 2, false, []), population.Capacity);
        delay.PrepareParticipants(population);
        for (var tick = 0; tick < 4; tick++) {
            body.SubmitIntent(default(PlayerIntent).WithChannel(forward, FixedQ4816.One));
            body.SetHeldChannels(default(PlayerIntent).WithChannel(button, FixedQ4816.One));
            delay.Apply(population);
        }
        var kept = default(PlayerIntent).WithChannel(forward, FixedQ4816.One);
        delay.InstallBarrier(0, kept, body.Channels);
        var restored = new WorldInputHoldRuntime(new(3, 1, 2, false, []), population.Capacity);
        restored.Restore(delay.Capture());
        body.SubmitIntent(default);
        body.SetHeldChannels(default);
        restored.Apply(population);
        var sample = body.TakeSubmittedInput();
        Assert.Equal(FixedQ4816.One, sample.Intent[forward]);
        Assert.Equal(FixedQ4816.Zero, sample.HeldChannels[button]);
        restored.InstallBarrier(0, default, body.Channels);
        restored.Apply(population);
        sample = body.TakeSubmittedInput();
        Assert.Equal(FixedQ4816.Zero, sample.Intent[forward]);
        Assert.Equal(FixedQ4816.Zero, sample.HeldChannels[button]);
    }

    [Fact]
    public void MidHoldAuthorityCheckpointAndWireRetainOriginAndHash() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with { ChannelsRaw = [.. basis.Channels, new("held", ChannelShape.Binary, Composition: true)],
            InputHistory = new(CalibrationLimitSeconds: 0.02m) };
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.JoinSeat();
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;
        Assert.True(catalog.TryGetLogicalId("held", out var channel));
        var owner = fixture.Server.Input.Admit(Principal.Seat(0), 0, [new(0, channel, WorldInputContributionKind.Level, "test:0")]);
        var interval = fixture.Server.Input.Interval;
        var batch = new WorldInputBatch(owner.Revision, owner.Epoch, owner.Mapping.Id, interval.Tick,
            default(PlayerIntent).WithChannel(owner.Mapping.Address(channel), FixedQ4816.One),
            new WorldInputContribution[] { new(0, channel, WorldInputContributionKind.Level, FixedQ4816.One.Value, 1) },
            new WorldInputEvent[] { new(channel, WorldInputEventKind.Press, FixedQ4816.One.Value, interval.Tick, FixedQ4816.FromRawBits(7), 1, 1) }, owner.Mapping.NumericCount);
        var writer = new WireWriter();
        Assert.True(WorldWireCodec.TryWriteInputBatch(writer, batch));
        var reader = new WireReader(writer.ToArray());
        var decodedBatch = WorldWireCodec.ReadInputBatch(ref reader, 1, 128);
        Assert.True(reader.TryFinish(out var wireReason), wireReason.ToString());
        var admission = fixture.Server.ApplyIntentSubmission(fixture.Server.Body(0)!, new(interval.Tick, 0, default, Principal.Seat(0), Input: decodedBatch));
        Assert.True(admission.IsAccepted, admission.InputRefusal);
        var stale = fixture.Server.ApplyIntentSubmission(fixture.Server.Body(0)!, new(interval.Tick, 0, default, Principal.Seat(0), Input: decodedBatch with { Epoch = decodedBatch.Epoch + 1 }));
        Assert.True(stale.Permission.IsAllowed);
        Assert.False(stale.IsAccepted);
        Assert.Contains("stale", stale.InputRefusal);
        Assert.True(owner.TryHeld(channel, out var held));
        Assert.True(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var captured, out var reason), reason);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(captured!), out var decoded, out reason), reason);
        using var restored = Fixtures.FreshServer(definition: definition);
        restored.Server.RestoreCheckpoint(decoded!);
        Assert.True(restored.Server.Input.TryGet(Principal.Seat(0), out var restoredOwner));
        Assert.True(restoredOwner!.TryHeld(channel, out var restoredHold));
        Assert.Equal(held, restoredHold);
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(fixture.Server, interval.Tick),
            WorldStateHashComposition.HashAuthoritative(restored.Server, interval.Tick));
        WorldInputSource[] sources = [new(0, channel, WorldInputContributionKind.Level, "test:0")];
        var epoch = restoredOwner.Epoch;
        restored.Server.Input.Reconfigure(definition with { ChannelsRaw = definition.Channels.ToArray() });
        Assert.Equal(epoch, restoredOwner.Epoch);
        Assert.True(restoredOwner.TryHeld(channel, out _));
        Assert.True(restored.Server.Input.TryAdmitProfile(Principal.Seat(0), 0, sources, out reason), reason);
        Assert.Equal(epoch, restoredOwner.Epoch);
        Assert.True(restoredOwner.TryHeld(channel, out _));
        Assert.True(restored.Server.Input.TryAdmitProfile(Principal.Seat(0), 0, sources, out reason, freshAttachment: true), reason);
        Assert.True(restoredOwner.Epoch > epoch);
        Assert.False(restoredOwner.TryHeld(channel, out _));
        Assert.True(restoredOwner.Sources[0].AwaitingNeutral);
        Assert.True(restoredOwner.TryEvent(channel, WorldInputEventKind.Cancel, 0, out var cancellation));
        Assert.Equal(held.Press, cancellation.HoldPress);
        Assert.True(restored.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var cancelledCheckpoint, out reason), reason);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(cancelledCheckpoint!), out var cancelledDecoded, out reason), reason);
        using var cancelledRestore = Fixtures.FreshServer(definition: definition);
        cancelledRestore.Server.RestoreCheckpoint(cancelledDecoded!);
        Assert.True(cancelledRestore.Server.Input.TryGet(Principal.Seat(0), out var cancelledOwner));
        Assert.True(cancelledOwner!.TryEvent(channel, WorldInputEventKind.Cancel, 0, out var savedCancellation));
        Assert.Equal(cancellation, savedCancellation);
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(restored.Server, interval.Tick),
            WorldStateHashComposition.HashAuthoritative(cancelledRestore.Server, interval.Tick));
        var cancelledEpoch = cancelledOwner.Epoch;
        Assert.False(cancelledRestore.Server.Input.TryCalibrate(Principal.Seat(0), FixedQ4816.One, out reason));
        Assert.Equal(cancelledEpoch, cancelledOwner.Epoch);
        Assert.True(cancelledRestore.Server.Input.TryCalibrate(Principal.Seat(0), FixedQ4816.FromRawBits(32), out reason), reason);
        Assert.Equal(cancelledEpoch + 1, cancelledOwner.Epoch);
        Assert.Equal(32, cancelledOwner.Calibration.Value);
        var grant = new WorldGrant(Principal.Seat(0), WorldCapability.Drive, GrantSubject.Body(0), false);
        fixture.Server.Revoke(grant, Principal.Console);
        Assert.False(owner.TryHeld(channel, out _));
        Assert.True(owner.TryEvent(channel, WorldInputEventKind.Cancel, 0, out var revoked));
        Assert.Equal(held.Press, revoked.HoldPress);
        fixture.Server.Grant(grant, Principal.Console);
        Assert.False(owner.TryHeld(channel, out _));
        Assert.True(owner.Sources[0].AwaitingNeutral);
    }

    private static (WorldInputOwner Owner, WorldInputCatalog Catalog) Create(int capacity = 256) {
        var catalog = WorldInputCatalog.Compile([
            new("hold", ChannelShape.Binary, Composition: true),
            new("text", ChannelShape.Text, Composition: true),
        ], new WorldInputModes(["hold", "text"], [new("focus", new(WorldInputModeSelectorKind.SeatState, "focus"), "a",
            [new("a", []), new("b", [])], Transitions: [new("a", "b", ["hold"])])]));
        return (new WorldInputOwner(catalog, catalog.Mappings[0], new(EventCapacity: capacity, MaximumEventsPerStep: capacity), [new(0, 0, WorldInputContributionKind.Level, "test:0")]), catalog);
    }
    private static WorldInputEvent Edge(uint sequence, bool press, ulong tick = 1, long offset = 0) =>
        new(0, press ? WorldInputEventKind.Press : WorldInputEventKind.Release, press ? FixedQ4816.One.Value : 0, tick, FixedQ4816.FromRawBits(offset), sequence, sequence);
    private static WorldInputBatch Batch(WorldInputOwner owner, WorldInputEvent[] events, bool held = false, ulong tick = 1) =>
        new(owner.Revision, owner.Epoch, owner.Mapping.Id, tick, default(PlayerIntent).WithChannel(owner.Mapping.Address(0), held ? FixedQ4816.One : FixedQ4816.Zero),
            new WorldInputContribution[] { new(0, 0, WorldInputContributionKind.Level, held ? FixedQ4816.One.Value : 0, 1) }, events);

    [Fact]
    public void PressAndReleaseWithinOneTickRetainBothTimesAndOrigin() {
        var (owner, _) = Create();
        var interval = new WorldInputInterval(1, 0, 1680);
        Assert.True(owner.TryAdmit(Batch(owner, [Edge(1, true, offset: 1), Edge(2, false, offset: 2)]), interval, [], out var reason), reason);
        Assert.Equal(2, owner.Count);
        Assert.Equal(owner.Record(0).Id, owner.Record(1).HoldPress);
        Assert.True(owner.Record(0).Time(WorldInputJudgement.Capture) < owner.Record(1).Time(WorldInputJudgement.Capture));
        Assert.False(owner.TryHeld(0, out _));
    }

    [Fact]
    public void KeptHoldRetainsOriginWithoutNewEdgeAndReleaseClosesIt() {
        var (owner, catalog) = Create();
        Assert.True(owner.TryAdmit(Batch(owner, [Edge(1, true)], true), new(1, 0, 1680), [], out var reason), reason);
        Assert.True(owner.TryHeld(0, out var original));
        owner.Switch(catalog.Mappings[1], new(1, 0, 1680), consumedActivation: 1);
        Assert.True(owner.TryHeld(0, out var kept));
        Assert.Equal(original.Press, kept.Press);
        Assert.Equal(original.Origin, kept.Origin);
        Assert.True(kept.Consumed);
        Assert.Equal(1, owner.SwitchCount);
        var decision = Assert.Single(owner.SwitchRecord(0).Decisions.ToArray());
        Assert.Equal(WorldInputContinuationReason.Kept, decision.Reason);
        Assert.Equal(original.Press, decision.HoldPress);
        Assert.False(owner.TryEvent(0, WorldInputEventKind.Press, 0, out _));
        Assert.True(owner.TryAdmit(Batch(owner, [Edge(1, false, 2)], tick: 2), new(2, 1680, 1680), [], out reason), reason);
        Assert.Equal(original.Press, owner.Record(1).HoldPress);
    }

    [Fact]
    public void ReleasedSourceNeedsExplicitNeutralAndStaleEpochCannotAct() {
        var (owner, catalog) = Create();
        Assert.True(owner.TryAdmit(Batch(owner, [Edge(1, true)], true), new(1, 0, 1680), [], out _));
        var stale = Batch(owner, [], true, 2);
        owner.Switch(catalog.Mappings[1], new(1, 0, 1680), reset: true);
        Assert.False(owner.TryHeld(0, out _));
        Assert.True(owner.TryEvent(0, WorldInputEventKind.Cancel, 0, out var cancellation));
        Assert.Equal(owner.Record(0).Id, cancellation.HoldPress);
        Assert.False(owner.TryEvent(0, WorldInputEventKind.Release, 0, out _));
        Assert.False(owner.TryAdmit(stale, new(2, 1680, 1680), [], out var reason));
        Assert.Contains("stale", reason);
        Assert.False(owner.TryAdmit(Batch(owner, [Edge(1, true, 2)], true, 2), new(2, 1680, 1680), [], out reason));
        Assert.Contains("neutral", reason);
        Assert.True(owner.TryAdmit(Batch(owner, [], tick: 2), new(2, 1680, 1680), [], out reason), reason);
        Assert.True(owner.TryAdmit(Batch(owner, [Edge(1, true, 3)], true, 3), new(3, 3360, 1680), [], out reason), reason);
    }

    [Fact]
    public void TextPastePreservesEqualTimeOrderAndRefusesNonScalarsAtomically() {
        var (owner, _) = Create();
        WorldInputEvent[] text = [new(1, WorldInputEventKind.Text, 'h', 1, default, 1, 0), new(1, WorldInputEventKind.Text, 'i', 1, default, 2, 0), new(1, WorldInputEventKind.Text, 0x1f642, 1, default, 3, 0)];
        Assert.True(owner.TryAdmit(Batch(owner, text), new(1, 0, 1680), [], out var reason), reason);
        Assert.Equal(new long[] { 'h', 'i', 0x1f642 }, Enumerable.Range(0, owner.Count).Select(i => owner.Record(i).Event.RawValue));
        Assert.False(owner.TryAdmit(Batch(owner, [new(1, WorldInputEventKind.Text, 0xd800, 2, default, 4, 0)], tick: 2), new(2, 1680, 1680), [], out reason));
        Assert.Contains("Unicode scalar", reason);
        Assert.Equal(3, owner.Count);
    }

    [Fact]
    public void SteadyHeldAdmissionAllocatesNothing() {
        var (owner, _) = Create();
        Assert.True(owner.TryAdmit(Batch(owner, [Edge(1, true)], true), new(1, 0, 1680), [], out _));
        var batch = Batch(owner, [], true, 2);
        for (var i = 0; i < 100; i++) { Assert.True(owner.TryAdmit(batch, new(2, 1680, 1680), [], out _)); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) {
            if (!owner.TryAdmit(batch, new(2, 1680, 1680), [], out _)) { throw new InvalidOperationException(); }
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
