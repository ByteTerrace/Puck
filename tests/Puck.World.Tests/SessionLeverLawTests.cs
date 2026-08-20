using Xunit;

using Puck.Launcher;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the session-lever door is keyed by NAME, end to end. The token the verb speaks is what the
/// codec carries, what the applier resolves a registered setter by, and what an unregistered name is refused as —
/// so a knob is one registration entry rather than an enum member plus two hand-synchronized switch arms. Also pins
/// the per-seat dimension the binding-bar knob needs, and that every lever (that one included) is checked against
/// <see cref="WorldCapability.Mutate"/> over the section it folds into before any client sees it.
/// </summary>
public sealed class SessionLeverLawTests {
    private const string KnobName = "law-knob";

    private static WorldSessionLeverSink ComposeShipped(WorldBindingBarVisibility visibility) =>
        WorldSessionLevers.Compose(
            audio: new RecordingAudioLever(),
            bindingBar: visibility,
            pacing: new PresentPacingControl(initialTargetHertz: null),
            settings: new WorldRenderSettings(defaults: Fixtures.BuildDocument().Render)
        );

    // ---- The registry ----

    [Fact]
    public void ARegisteredNameApplies_AndAnUnregisteredOneIsRefusedRatherThanDropped() {
        var sink = new WorldSessionLeverSink();
        var seen = new List<double>();

        sink.Register(
            name: KnobName,
            setter: lever => seen.Add(item: lever.A)
        );

        Assert.True(condition: sink.TryApply(lever: Lever(
            name: KnobName,
            a: 4.25
        )));
        Assert.Equal(expected: [4.25], actual: seen);
        // The discriminating half: a name nobody registered answers false, so the caller can say so out loud
        // instead of a write silently going nowhere.
        Assert.False(condition: sink.TryApply(lever: Lever(
            name: "not-a-knob",
            a: 1.0
        )));
        Assert.Single(collection: seen);
        Assert.True(condition: sink.IsRegistered(name: KnobName));
        Assert.False(condition: sink.IsRegistered(name: "not-a-knob"));
    }
    [Fact]
    public void ADuplicateRegistrationRefuses_RatherThanShadowingTheLiveKnob() {
        var sink = new WorldSessionLeverSink();

        sink.Register(
            name: KnobName,
            setter: static _ => { }
        );

        _ = Assert.Throws<ArgumentException>(testCode: () => sink.Register(
            name: KnobName,
            setter: static _ => { }
        ));
        // A different name still registers, so the refusal is about the collision rather than the second call.
        sink.Register(
            name: "other-knob",
            setter: static _ => { }
        );
    }
    [Fact]
    public void TheShippedCompositionRegistersExactlyTheTokensTheVerbsSpeak() {
        var sink = ComposeShipped(visibility: new WorldBindingBarVisibility());
        string[] expected = [
            WorldSessionLevers.AmbientOcclusion,
            WorldSessionLevers.AmbientOcclusionQuality,
            WorldSessionLevers.BindingBar,
            WorldSessionLevers.FarBound,
            WorldSessionLevers.MasterVolume,
            WorldSessionLevers.RenderScale,
            WorldSessionLevers.ShadowAccumulation,
            WorldSessionLevers.ShadowFarExit,
            WorldSessionLevers.ShadowMarch,
            WorldSessionLevers.ShadowMask,
            WorldSessionLevers.Shadows,
            WorldSessionLevers.TargetHertz,
            WorldSessionLevers.UpscaleSharpness,
        ];

        Assert.Equal(
            actual: sink.Names.Order(comparer: StringComparer.Ordinal),
            expected: expected.Order(comparer: StringComparer.Ordinal)
        );
    }

    // ---- The per-seat dimension ----

    [Fact]
    public void TheBindingBarKnobWritesTheSeatTheLeverNames_AndAutoClearsIt() {
        var visibility = new WorldBindingBarVisibility();
        var sink = ComposeShipped(visibility: visibility);

        Assert.False(condition: visibility.Engaged);

        sink.Apply(lever: BarLever(
            a: 0.0,
            seat: 2
        ));

        Assert.False(condition: visibility.Override(slot: 2));
        // Seat-scoped, not session-wide: a sibling seat is untouched by its neighbour's write.
        Assert.Null(@object: visibility.Override(slot: 1));
        Assert.True(condition: visibility.Engaged);

        sink.Apply(lever: BarLever(
            a: 1.0,
            seat: 2
        ));

        Assert.True(condition: visibility.Override(slot: 2));

        sink.Apply(lever: BarLever(
            a: WorldSessionLevers.BindingBarAuto,
            seat: 2
        ));

        Assert.Null(@object: visibility.Override(slot: 2));
        Assert.False(condition: visibility.Engaged);
    }
    [Fact]
    public void AnOutOfRangeSeatDropsTheWriteRatherThanThrowingThroughTheDeliveryPath() {
        var visibility = new WorldBindingBarVisibility();
        var sink = ComposeShipped(visibility: visibility);

        sink.Apply(lever: BarLever(
            a: 1.0,
            seat: PlayerRoster.MaxSlots
        ));
        sink.Apply(lever: BarLever(
            a: 1.0,
            seat: WorldSessionLever.NoSeat
        ));

        Assert.False(condition: visibility.Engaged);
        // The control: an in-range seat on the same sink still lands, so the drops above are about the seat.
        sink.Apply(lever: BarLever(
            a: 1.0,
            seat: 0
        ));

        Assert.True(condition: visibility.Override(slot: 0));
    }

    // ---- The wire ----

    [Fact]
    public void TheLeafRoundTripsTheNameAndTheSeat() {
        var lever = new WorldSessionLever(
            A: 0.5,
            B: 12.25,
            Name: WorldSessionLevers.BindingBar,
            Seat: 3,
            Section: WorldSection.Bindings
        );

        Assert.True(condition: WorldSubmissionCodec.TryEncodeLever(
            bytes: out var bytes,
            failure: out var encodeFailure,
            lever: lever
        ), userMessage: encodeFailure.Detail);
        Assert.True(condition: WorldSubmissionCodec.TryDecodeLever(
            bytes,
            lever: out var decoded,
            failure: out var decodeFailure
        ), userMessage: decodeFailure.Detail);
        Assert.Equal(expected: lever, actual: decoded);
    }
    [Fact]
    public void AnEmptyNameIsRefusedOnTheWire_BecauseItCanAddressNoRegistration() {
        Assert.True(condition: WorldSubmissionCodec.TryEncodeLever(
            bytes: out var bytes,
            failure: out _,
            lever: Lever(
            a: 1.0,
            name: string.Empty
        )
        ));
        Assert.False(condition: WorldSubmissionCodec.TryDecodeLever(
            bytes,
            lever: out _,
            failure: out var failure
        ));
        Assert.Equal(expected: WorldCodecRefusal.PayloadMalformed, actual: failure.Refusal);
        // The control: one character of name is enough to address a registration, so the refusal is about
        // emptiness rather than about the leaf's shape.
        Assert.True(condition: WorldSubmissionCodec.TryEncodeLever(
            bytes: out var named,
            failure: out _,
            lever: Lever(
            a: 1.0,
            name: "x"
        )
        ));
        Assert.True(condition: WorldSubmissionCodec.TryDecodeLever(
            named,
            lever: out _,
            failure: out _
        ));
    }

    // ---- The grant gate ----

    [Fact]
    public void TheBindingBarLeverIsCheckedAgainstMutateOverItsSection_LikeEveryOtherLever() {
        using var fixture = Fixtures.FreshServer();
        var sink = new RecordingClientSink();
        // Actor and target differ: seat 0 drives the lever, seat 1's bar is what it moves.
        var actor = WorldPrincipal.Seat(slot: 0);
        var subject = GrantSubject.Section(section: WorldSection.Bindings);
        var hold = new WorldGrant(
            Capability: WorldCapability.Mutate,
            Exclusive: false,
            Principal: actor,
            Subject: subject
        );

        using var lease = fixture.Server.AttachSink(sink: sink);

        // The control: a seat's seeded hold over the bar's own section carries the lever through whole.
        fixture.Server.ApplySessionLever(
            lever: BarLever(
                a: 0.0,
                seat: 1
            ),
            principal: actor
        );

        var accepted = Assert.Single(collection: sink.Levers);

        Assert.Equal(expected: WorldSessionLevers.BindingBar, actual: accepted.Name);
        Assert.Equal(expected: 1, actual: accepted.Seat);

        // One grant different: revoking the section refuses the identical lever before any client sees it.
        fixture.Server.Revoke(
            actor: WorldPrincipal.Console,
            grant: hold
        );

        Assert.False(condition: fixture.Server.Grants.Allows(
            capability: WorldCapability.Mutate,
            principal: actor,
            subject: subject
        ));

        fixture.Server.ApplySessionLever(
            lever: BarLever(
                a: 1.0,
                seat: 1
            ),
            principal: actor
        );

        Assert.Single(collection: sink.Levers);
    }

    private static WorldSessionLever BarLever(double a, int seat) => new(
        A: a,
        Name: WorldSessionLevers.BindingBar,
        Seat: seat,
        Section: WorldSection.Bindings
    );
    private static WorldSessionLever Lever(string name, double a) => new(
        A: a,
        Name: name,
        Section: WorldSection.Render
    );

    private sealed class RecordingAudioLever : IWorldAudioLever {
        public void SetMasterVolume(float value) {
        }
    }
    private sealed class RecordingClientSink : IClientSink {
        public List<WorldSessionLever> Levers { get; } = [];

        public void DeliverAnswer(in QueryAnswer answer) {
        }
        public void DeliverComposition(WorldComposition composition) {
        }
        public void DeliverDefinition(WorldDefinition definition) {
        }
        public void DeliverSessionLever(WorldSessionLever lever) => Levers.Add(item: lever);
        public void DeliverSnapshot(in WorldSnapshot snapshot) {
        }
    }
}
