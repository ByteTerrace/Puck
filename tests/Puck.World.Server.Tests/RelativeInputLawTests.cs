using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class RelativeInputLawTests {
    [InlineData(30)]
    [InlineData(60)]
    [Theory]
    public void TinyDisplacementsSurviveScalarConversionWithoutRateScaling(int rate) {
        var definition = Fixtures.BuildDocumentAtRate(rateHz: rate);

        definition = definition with { ChannelsRaw = [.. definition.Channels, new WorldChannel("delta", ChannelShape.Relative, Composition: true, Relative: new(MaximumPerStep: 10, Sensitivity: 1, Unit: "units"))] };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var principal = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(IdentityName: null, Principal: principal, Slot: 0, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        var body = fixture.Server.Body(index: 0)!;

        Assert.True(condition: fixture.Server.Population.Channels.TryGetOrdinal(name: "delta", ordinal: out var ordinal));
        var total = FixedQ4816.Zero;
        WorldBodyTransferState halfway = default;

        for (var index = 0; (index < 16); index++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithRelative(ordinal: ordinal, value: FixedQ3232.FromRawBits(value: 4096)));
            fixture.Step();
            total += body.ReadChannelScalar(ordinal: ordinal);
            if (index == 7) {
                halfway = body.CaptureTransferState();
                fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertKit(Principal: Principal.Console, Kit: definition.Kits[0] with { Mass = 1f }));
                fixture.Step();
                Assert.Equal(halfway.RelativeRemainders[ordinal], body.CaptureTransferState().RelativeRemainders[ordinal]);
            }
        }
        Assert.Equal(FixedQ4816.Epsilon, total);
        Assert.Equal(0, body.CaptureTransferState().RelativeRemainders[ordinal]);
        fixture.Step();
        Assert.Equal(FixedQ4816.Zero, body.ReadChannelScalar(ordinal: ordinal));
        body.ApplyTransferState(state: halfway);
        var resumed = FixedQ4816.Zero;

        for (var index = 0; (index < 8); index++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithRelative(ordinal: ordinal, value: FixedQ3232.FromRawBits(value: 4096)));
            fixture.Step();
            resumed += body.ReadChannelScalar(ordinal: ordinal);
        }
        Assert.Equal(FixedQ4816.Epsilon, resumed);
    }
}
