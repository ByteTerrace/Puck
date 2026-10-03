using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a traveller's committed motion has one encoding. A checkpoint holding an in-doubt transfer
/// and a federation commit carry the same member to the same decoded state, field for field, whether its action
/// continuity carries edges and registers or none (an absent continuity is the empty one, the only way to carry none).
/// Both refuse a continuum segment that is not well formed.
/// </summary>
public sealed partial class WorldAuthorityCheckpointCodecLawTests {
    private static readonly WorldContinuumTrajectory Segment = new(
        BoundaryEvents: 2,
        ConsumedThroughEngineTick: 160,
        ContinuumEndEngineTick: 150,
        ContinuumStartEngineTick: 100,
        PreviousPosition: new FixedVector3(X: FixedQ4816.FromInteger(value: 5), Y: FixedQ4816.One, Z: FixedQ4816.FromInteger(value: -2)),
        SourceTick: 9
    );

    // A member with every motion field set apart from its defaults.
    private static WorldTransferCommitMember EveryField(WorldTransferActionContinuity? continuity) => new(
        ActionContinuity: continuity,
        BodyMotionProgramName: "walk",
        Continuum: Segment,
        HasMappedArrival: true,
        PlanarVelocity: new FixedVector3(X: FixedQ4816.FromInteger(value: 1), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: 0.5)),
        Position: new FixedVector3(X: FixedQ4816.FromInteger(value: 2), Y: FixedQ4816.FromDouble(value: 0.25), Z: FixedQ4816.FromInteger(value: 3)),
        Profile: null,
        TravelTurn: FixedQ4816.FromDouble(value: 0.75),
        VerticalVelocity: FixedQ4816.FromDouble(value: -1.5),
        YawRadians: FixedQ4816.FromDouble(value: 1.25)
    );
    private static WorldTransferActionContinuity Carried { get; } = new(
        Channels: [
            new WorldTransferChannelEdge(HeldValue: FixedQ4816.One, Name: "move", PreviousBit: true),
            new WorldTransferChannelEdge(HeldValue: FixedQ4816.FromDouble(value: -0.5), Name: "strafe", PreviousBit: false),
        ],
        Registers: [new WorldTransferActionRegister(Kind: ActionStateKind.Timer, Name: "dash", TimerTicks: 42UL, Value: FixedQ4816.FromInteger(value: 3))]
    );

    // The whole member as text, its continuity's edges and registers spelled out, so two members describe alike
    // exactly when every field is equal.
    private static string Describe(WorldTransferCommitMember member) => string.Join(
        separator: " | ",
        values: [
            (member with { ActionContinuity = WorldTransferActionContinuity.Empty }).ToString(),
            string.Join(separator: ", ", values: member.ActionContinuity.Channels),
            string.Join(separator: ", ", values: member.ActionContinuity.Registers),
        ]
    );
    private static WorldTransferCommitMember ThroughTheCheckpoint(WorldTransferCommitMember member) {
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: Capture(commitMember: member));

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: encoded, checkpoint: out var decoded, reason: out var reason), userMessage: reason);

        return decoded!.HostRow.InDoubtTransfers[0].CommitMembers[0];
    }
    private static WorldTransferCommitMember ThroughTheFederation(WorldTransferCommitMember member) {
        var encoded = WorldFederationCodec.EncodeCommit(members: [member], sourceAuthority: "row-a", transferId: 7UL);

        Assert.True(condition: WorldFederationCodec.TryDecodeCommit(body: encoded, failure: out var failure, members: out var decoded, sourceAuthority: out _, transferId: out _), userMessage: failure.ToString());

        return Assert.Single(collection: decoded);
    }

    [Theory]
    [InlineData("carried")]
    [InlineData("empty")]
    [InlineData("absent")]
    public void TheCheckpointAndTheFederationCommitCarryAMemberTheSameWay(string continuity) {
        var member = EveryField(continuity: continuity switch {
            "carried" => Carried,
            "empty" => WorldTransferActionContinuity.Empty,
            _ => null,
        });
        var expected = Describe(member: member);

        Assert.Equal(expected: expected, actual: Describe(member: ThroughTheCheckpoint(member: member)));
        Assert.Equal(expected: expected, actual: Describe(member: ThroughTheFederation(member: member)));
    }
    [Fact]
    public void BothCarriersRefuseAContinuumThatIsNotWellFormed() {
        // An interval that ends where it starts; the well-formed control above decodes through both carriers.
        var member = (EveryField(continuity: Carried) with { Continuum = (Segment with { ContinuumEndEngineTick = Segment.ContinuumStartEngineTick }) });
        var checkpoint = WorldAuthorityCheckpointCodec.Encode(checkpoint: Capture(commitMember: member));
        var commit = WorldFederationCodec.EncodeCommit(members: [member], sourceAuthority: "row-a", transferId: 7UL);

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: checkpoint, checkpoint: out _, reason: out var reason));
        Assert.Contains(expectedSubstring: "continuum trajectory has invalid interval", actualString: reason);
        Assert.False(condition: WorldFederationCodec.TryDecodeCommit(body: commit, failure: out var failure, members: out _, sourceAuthority: out _, transferId: out _));
        Assert.Contains(expectedSubstring: "continuum trajectory has invalid interval", actualString: failure.ToString());
    }
}
