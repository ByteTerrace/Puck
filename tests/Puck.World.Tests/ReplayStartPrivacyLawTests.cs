using System.Text;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a tape that starts from a checkpoint carries no owned identity document. A seat seated on an owned
/// identity, whose document holds rows only that document names, is recorded from a mid-run arm; the tape re-drives
/// clean, and its bytes carry none of the document's private markers, though the live checkpoint the tape started from
/// does — the identity crosses onto the tape as its projection alone, the way the tape's seats always have.
/// </summary>
public sealed class ReplayStartPrivacyLawTests {
    private static bool CarriesAnyMarker(byte[] bytes) {
        var text = Encoding.UTF8.GetString(bytes: bytes);

        return CrossingIdentityPrivacyLawTests.Markers.Any(predicate: marker => text.Contains(comparisonType: StringComparison.Ordinal, value: marker));
    }

    [Fact]
    public void AMidRunTapesBytesCarryNoOwnedDocument() {
        using var directory = new TemporaryDirectory(prefix: "puck-replay-start-privacy-");
        using var fixture = Fixtures.FreshServer();
        var catalog = fixture.Server.Profiles;

        Assert.True(condition: catalog.ReplaceFromSync(document: CrossingIdentityFixtures.OwnedDocument(), reason: out var reason), userMessage: reason);
        var owned = CrossingIdentityFixtures.Owned(identity: catalog.FindById(id: CrossingIdentityFixtures.OwnerId));

        Assert.True(condition: catalog.TrySave(identity: owned, reason: out reason), userMessage: reason);
        _ = fixture.JoinSeat(slot: 0);
        fixture.Server.Population.SetSeatProfile(profile: owned, slot: 0);

        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: fixture.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: catalog,
            stateRoot: new WorldStateRoot(path: directory.RootPath),
            transport: new LoopbackTransport(server: fixture.Server)
        );

        for (var tick = 0; (tick < 5); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        // The control: the live checkpoint at the arm holds the owned document, so the search below is a real one.
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var live,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out reason
            ),
            userMessage: reason
        );
        Assert.True(condition: CarriesAnyMarker(bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: live!)));

        Assert.True(condition: tape.TryBeginRecording(name: "owned-seat", refusal: out var refusal), userMessage: refusal);
        for (var tick = 0; (tick < 5); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        var stop = tape.StopRecording();

        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());
        Assert.False(condition: CarriesAnyMarker(bytes: File.ReadAllBytes(path: stop.Path)), userMessage: "the mid-run tape carries a private marker of the owned document");
    }
}
