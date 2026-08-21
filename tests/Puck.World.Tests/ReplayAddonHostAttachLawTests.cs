using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// A re-drive's addon host attach is structural, not a factory side effect: <see cref="WorldReplaySnapshot.Drive"/>
/// itself attaches whatever <c>addonHostFactory</c> returns to the shadow server, so a replayed addon-affecting
/// mutation's prepare gate (<see cref="IWorldAddonHost.TryPrepare"/>) reaches that host rather than a null one,
/// regardless of whether the factory self-attached.
/// </summary>
public sealed class ReplayAddonHostAttachLawTests {
    [Fact]
    public void ReplayedMutationReachesTheFactorysHostEvenWhenItDoesNotSelfAttach() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();

        // A server with NO addon host attached refuses an addon-affecting mutation by name (see
        // AddonPrepareGateLawTests.NoAddonHostAttachedRefusesAnAddonAffectingMutation...), so the live side needs
        // its OWN permissive host attached for this mutation to apply at all — a SEPARATE instance from the one
        // Drive attaches below, so the discriminator this law actually tests (whether Drive reaches the FACTORY's
        // host) stays meaningful.
        fixture.Server.AttachAddons(runtime: new NullAddonHost());

        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: [], addonHostFactory: static (_, _) => new NullAddonHost());

        Assert.True(condition: tape.TryBeginRecording(name: $"g-addon-attach-{Guid.NewGuid():N}", refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertAddon(
            Addon: new WorldAddonRow(Name: "probe", ModulePath: "nonexistent.wasm", Hash: "sha256-64/deadbeefdeadbeef", Fuel: 1000UL, Enabled: true),
            Principal: WorldPrincipal.Console
        ));

        fixture.Step();
        tape.NoteTick();

        var result = tape.StopRecording();

        Assert.Null(result.VerifyFault);

        using var persistedStream = File.OpenRead(path: result.Path);
        var persisted = WorldReplaySnapshot.Read(stream: persistedStream);

        // This factory deliberately does NOT self-attach (the same shape NullAddonHost's own callers rely on) —
        // Drive itself must be the one that attaches it. The returned instance is captured so the assertion below
        // can prove THIS EXACT object's TryPrepare was reached, never merely that some host somewhere ran.
        var capturedHost = new NullAddonHost();

        _ = persisted.Drive(
            profiles: fixture.Server.Profiles,
            engines: [],
            addonHostFactory: (_, _) => capturedHost
        );

        // THE DISCRIMINATOR: before the structural attach, the shadow server's addon host stayed null for a
        // factory that does not self-attach, so a replayed addon-affecting mutation's prepare gate found
        // m_addons null and applied vacuously without ever touching this object. After the fix, Drive attaches the
        // factory's host itself, so the replayed mutation's prepare gate reaches THIS instance.
        Assert.True(condition: (capturedHost.PrepareCallCount > 0), userMessage: "the replayed UpsertAddon mutation never reached the factory's own addon host — Drive is not structurally attaching it");
    }
}
