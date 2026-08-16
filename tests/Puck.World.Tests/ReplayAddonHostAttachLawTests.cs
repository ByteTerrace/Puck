using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// A re-drive's addon host attach is structural, not a factory side effect: <see cref="WorldReplaySnapshot.Drive"/>
/// itself attaches whatever <c>addonHostFactory</c> returns to the shadow server, so a replayed
/// <c>WorldAddonLifecycle.Mount</c> reaches that host rather than a null one regardless of whether the factory
/// self-attached.
/// </summary>
public sealed class ReplayAddonHostAttachLawTests {
    [Fact]
    public void ReplayedAddonLifecycle_ReachesTheFactorysHostEvenWhenItDoesNotSelfAttach() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: [], addonHostFactory: static (_, _) => new NullAddonHost());
        var name = $"g-addon-attach-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        transport.SubmitAddonLifecycle(
            lifecycle: new WorldAddonLifecycle.Mount(Name: "probe", ModulePath: "nonexistent.wasm", Hash: "sha256-64/deadbeefdeadbeef", Fuel: 1000UL, Requests: null),
            principal: WorldPrincipal.Console
        );

        fixture.Step();
        tape.NoteTick();

        var result = tape.StopRecording();

        Assert.Null(result.VerifyFault);

        using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));
        var persisted = WorldReplaySnapshot.Read(stream: stream);
        var messages = new List<string>();

        // This factory deliberately does NOT self-attach (the same shape NullAddonHost's own callers rely on) —
        // Drive itself must be the one that attaches it.
        _ = persisted.Drive(
            profiles: fixture.Server.Profiles,
            engines: [],
            addonHostFactory: (_, shadow) => {
                shadow.EchoTap = echo => messages.Add(item: echo.Message);

                return new NullAddonHost();
            }
        );

        // THE DISCRIMINATOR: before the structural attach, the shadow server's addon host stayed null for a
        // factory that does not self-attach, so the replayed mount refused with the null-host message. After the
        // fix, Drive attaches the factory's host itself, so the same replayed mount instead reaches
        // NullAddonHost.Mount and refuses with ITS message.
        Assert.Contains(collection: messages, filter: message => message.Contains(value: "no addon host is mounted (NullAddonHost)"));
        Assert.DoesNotContain(collection: messages, filter: message => message.Contains(value: "there is no runtime to mount into"));
    }
}
