using Puck.Commands;
using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Proves the typed-narration seam directly against a fresh in-process server: a sink attached through
/// <see cref="WorldServer.AttachNarrationSink"/> observes exactly one <see cref="WorldNarration"/> record for one
/// applied mutation, carrying the same channel tag and formatted line a direct <c>Console.Error.WriteLine</c> would
/// have produced — and, with no sink attached, the narration goes undelivered rather than falling back to
/// <c>Console.Error</c>.
/// </summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class WorldNarrationLawTests {
    [Fact]
    public void AttachedSink_ObservesOneMutationsNarrationRecordAndFormattedLine() {
        using var fixture = Fixtures.FreshServer();
        var sink = new RecordingNarrationSink();
        using var lease = fixture.Server.AttachNarrationSink(sink: sink);

        var grant = new WorldGrant(
            Grantee: Principal.Seat(slot: 0),
            Capability: WorldCapability.Drive,
            Subject: GrantSubject.Body(index: 0),
            Exclusive: false
        );

        fixture.Server.Grant(
            grant: grant,
            actor: Principal.Console
        );

        var narration = Assert.Single(collection: sink.Narrations);

        Assert.Equal(
            expected: "world.grant",
            actual: narration.Channel
        );
        Assert.StartsWith(
            expectedStartString: "[world.grant: ",
            actualString: narration.Text,
            comparisonType: StringComparison.Ordinal
        );
        Assert.EndsWith(
            expectedEndString: "]",
            actualString: narration.Text,
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: "seat1 drive body:0",
            actualString: narration.Text,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void DetachedLease_StopsReceivingLaterNarration() {
        using var fixture = Fixtures.FreshServer();
        var sink = new RecordingNarrationSink();
        var lease = fixture.Server.AttachNarrationSink(sink: sink);

        var first = new WorldGrant(
            Grantee: Principal.Seat(slot: 0),
            Capability: WorldCapability.Drive,
            Subject: GrantSubject.Body(index: 0),
            Exclusive: false
        );

        fixture.Server.Grant(
            grant: first,
            actor: Principal.Console
        );
        Assert.Single(collection: sink.Narrations);

        lease.Dispose();

        var second = new WorldGrant(
            Grantee: Principal.Seat(slot: 1),
            Capability: WorldCapability.Drive,
            Subject: GrantSubject.Body(index: 1),
            Exclusive: false
        );

        fixture.Server.Grant(
            grant: second,
            actor: Principal.Console
        );

        // Still exactly one — the lease's own dispose stopped this sink from observing the second grant's line.
        Assert.Single(collection: sink.Narrations);
    }
    // With no sink attached, not even the fixture's console sink, a mutation's narration reaches neither console
    // stream: the hub has no fallback writer. Swapping both streams is why this class runs in the console-redirection
    // collection.
    [Fact]
    public void NoSinkAttached_MutationsNarrationGoesUndelivered() {
        using var fixture = Fixtures.FreshServer(consoleNarration: false);
        var grant = new WorldGrant(
            Grantee: Principal.Seat(slot: 0),
            Capability: WorldCapability.Drive,
            Subject: GrantSubject.Body(index: 0),
            Exclusive: false
        );
        var originalError = Console.Error;
        var originalOut = Console.Out;
        using var error = new StringWriter();
        using var output = new StringWriter();

        try {
            Console.SetError(newError: error);
            Console.SetOut(newOut: output);
            fixture.Server.Grant(
                grant: grant,
                actor: Principal.Console
            );
        } finally {
            Console.SetError(newError: originalError);
            Console.SetOut(newOut: originalOut);
        }

        Assert.Contains(expected: (grant.Capability, grant.Subject), collection: fixture.Server.Grants.Held(grantee: grant.Grantee));
        Assert.Equal(expected: string.Empty, actual: error.ToString());
        Assert.Equal(expected: string.Empty, actual: output.ToString());
    }
}

/// <summary>A narration sink test double that records every delivered <see cref="WorldNarration"/> in order.</summary>
internal sealed class RecordingNarrationSink : IWorldNarrationSink {
    public List<WorldNarration> Narrations { get; } = [];

    public void Narrate(in WorldNarration narration) => Narrations.Add(item: narration);
}
