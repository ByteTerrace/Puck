using System.Numerics;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Executable law for the local half of cross-seam melee contact: two dynamic bodies are not allowed to
/// occupy one volume, and the stable pair order produces the same authoritative result on every run.</summary>
public sealed class DynamicBodyContactLawTests {
    [Fact]
    public void CoincidentBodiesSeparateDeterministically() {
        var first = ResolveCoincidentPair();
        var second = ResolveCoincidentPair();

        Assert.Equal(first, second);
        Assert.True(Vector3.Distance(value1: first.Left, value2: first.Right) >= 0.69f,
            userMessage: $"capsule pair remained overlapped at {first.Left} / {first.Right}");
    }

    private static (Vector3 Left, Vector3 Right) ResolveCoincidentPair() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildGradientUpDocument(gradientUp: false));
        var left = WorldPrincipal.Seat(slot: 0);
        var right = WorldPrincipal.Seat(slot: 1);
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(left, left.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(right, right.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var coincident = new Vector3(x: 10f, y: 5f, z: 10f);
        fixture.Server.Body(index: left.Index)!.Pose(x: coincident.X, y: coincident.Y, z: coincident.Z, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        fixture.Server.Body(index: right.Index)!.Pose(x: coincident.X, y: coincident.Y, z: coincident.Z, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        fixture.Step();
        return (fixture.Server.Body(index: left.Index)!.Position, fixture.Server.Body(index: right.Index)!.Position);
    }
}
