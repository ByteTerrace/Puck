using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: an owned identity's id names one live object, and a tape never sees an owned document. A pull
/// adopts a cloud copy in place, so every seat bound to the identity follows it; while a tape reading the catalog
/// records, a pull refuses by name and changes nothing, because the re-drive could not reproduce the change.
/// </summary>
public sealed class OwnedWorldPullLawTests {
    [Fact]
    public void APullWhileATapeRecordsRefusesByNameAndAfterwardAdoptsInPlace() {
        using var directory = new TemporaryDirectory(prefix: "puck-owned-pull-");
        using var fixture = Fixtures.FreshServer();
        var catalog = fixture.Server.Profiles;
        var owned = catalog.BootProfile;
        var authored = owned.Document!;
        var pulled = authored with { Identity = authored.Identity! with { Name = "Pulled" } };
        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: fixture.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: catalog,
            stateRoot: new WorldStateRoot(path: directory.RootPath),
            transport: new LoopbackTransport(server: fixture.Server)
        );

        Assert.True(condition: tape.TryBeginRecording(name: "pull", refusal: out var refusal), userMessage: refusal);
        Assert.True(condition: catalog.Recording);
        Assert.False(condition: catalog.ReplaceFromSync(document: pulled, reason: out var reason));
        Assert.StartsWith(expectedStartString: nameof(WorldOwnedWorldSyncRefusal.PullWhileRecording), actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Same(expected: authored, actual: owned.Document);
        Assert.NotEqual(expected: "Pulled", actual: owned.Name);

        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();

        // The control: with no recording, the same pull adopts, in place.
        Assert.False(condition: catalog.Recording);
        Assert.True(condition: catalog.ReplaceFromSync(document: pulled, reason: out reason), userMessage: reason);
        Assert.Same(expected: owned, actual: catalog.FindById(id: owned.Id));
        Assert.Same(expected: pulled, actual: owned.Document);
        Assert.Equal(expected: "Pulled", actual: owned.Name);
    }
}
