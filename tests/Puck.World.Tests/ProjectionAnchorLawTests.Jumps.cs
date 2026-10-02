using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class ProjectionAnchorLawTests {
    // Every way the authoritative state jumps rather than steps: a checkpoint restore, the three whole-document
    // rebuilds, an undo, and a replay drive, which rebuilds its boot image and restores its first keyframe. A
    // federation observer keeps its subscription through each, where a session observer is ended by a rebuild and
    // loses its observe grant to a restore, so the wire is the recipient that must follow every jump.
    [Theory]
    [InlineData("restore")]
    [InlineData("reset")]
    [InlineData("load")]
    [InlineData("reload")]
    [InlineData("undo")]
    [InlineData("replay")]
    public async Task Every_state_jump_reaches_a_federation_observer_whose_step_sends_anchors_alone(string jump) {
        var density = new BindableScalar(binding: $"state.{Clock}");
        var definition = Document(row: Row(advance: PerTick(raw: 37L), raw: 0L)) with {
            RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: density, Name: "haze")])),
            TimelineRaw = null,
        };
        using var directory = new TemporaryDirectory(prefix: "proj-jump-");
        var path = Path.Combine(path1: directory.RootPath, path2: "jump.world.json");
        var bytes = WorldDefinitionSerialization.Serialize(definition: definition);

        File.WriteAllBytes(bytes: bytes, path: path);

        using var fixture = Fixtures.FreshServer(definition: definition);
        var sink = new WorldFederationProjectionSink(
            authority: "boot",
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 1f)),
            revision: static () => 1,
            server: fixture.Server,
            tier: WorldDisclosureTier.Presentation
        );
        using var lease = fixture.Server.AttachSink(sink: sink);
        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: fixture.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: fixture.Server.Profiles,
            stateRoot: new WorldStateRoot(path: directory.RootPath),
            transport: new LoopbackTransport(server: fixture.Server)
        );
        var name = $"proj-jump-{Guid.NewGuid():N}";

        if (jump == "replay") {
            Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: refusal);
        }

        for (var index = 0; (index < 3); index++) {
            fixture.Step();
            tape.NoteTick();
        }

        if (jump == "replay") {
            _ = tape.StopRecording();
        }

        WorldAuthorityCheckpoint? checkpoint = null;

        if (jump == "restore") {
            Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out checkpoint, hostRow: WorldAuthorityHostRowCheckpoint.Empty, reason: out var reason), userMessage: reason);
        }

        long Read(WorldDefinition document) {
            var time = fixture.Server.Time;

            Assert.True(condition: WorldStateReader.TryReadEased(definition: document, engineTick: time.EngineTick, key: null, rawValue: out var raw, row: out _, rowName: Clock, text: out _, tick: time.Tick));

            return raw!.Value;
        }

        // The write every jump discards; the observer is sent it like any other write.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: Principal.Console, Row: Clock, Key: WorldStateRow.SlotKey.Value, Value: 458752L, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        var written = Read(document: fixture.Server.Definition);

        switch (jump) {
            case "restore":
                fixture.Server.RestoreCheckpoint(checkpoint: checkpoint!);
                break;
            case "reset":
                fixture.Server.EnqueueRebuild(principal: Principal.Console, request: new WorldRebuildRequest(Definition: null, Force: true, Kind: WorldRebuildKind.Reset, PathHint: null));
                fixture.Step();
                break;
            case "load" or "reload":
                fixture.Server.EnqueueRebuild(principal: Principal.Console, request: new WorldRebuildRequest(
                    ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: bytes),
                    Definition: definition,
                    Force: true,
                    Kind: ((jump == "reload") ? WorldRebuildKind.Reload : WorldRebuildKind.Load),
                    PathHint: path
                ));
                fixture.Step();
                break;
            case "undo":
                fixture.Server.EnqueueUndo(count: 1, principal: Principal.Console);
                fixture.Step();
                break;
            default:
                Assert.True(condition: tape.TryBeginDrive(documentPath: path, forkName: null, name: name, refusal: out var driveRefusal, toTick: null), userMessage: driveRefusal);
                break;
        }

        Assert.Null(@object: sink.DetachReason);

        using var wire = new MemoryStream();

        await sink.StreamAsync(ct: TestContext.Current.CancellationToken, output: wire);
        sink.Release();

        if (jump == "replay") {
            _ = tape.CancelDrive();
        }

        using var received = new MemoryStream(buffer: wire.ToArray());
        var hold = new WorldProjectionHold();

        while (received.Position < received.Length) {
            var frame = await WorldFederationCodec.ReadResponseAsync(ct: TestContext.Current.CancellationToken, stream: received);

            Assert.True(condition: frame.Ok);

            if (frame.Kind == ((byte)WorldFederationResponse.Definition)) {
                Assert.True(condition: WorldFederationCodec.TryDecodeDocument(body: frame.Body.Span, definition: out _, failure: out var failure, hold: hold, tier: out _, version: out _), userMessage: failure.ToString());
            } else if (frame.Kind == ((byte)WorldFederationResponse.ProjectionDelta)) {
                Assert.True(condition: WorldFederationCodec.TryDecodeProjectionDelta(body: frame.Body.Span, definition: out _, failure: out var failure, hold: hold, stamp: out _, valuesOnly: out _, version: out _), userMessage: failure.ToString());
            }
        }

        var authority = Read(document: fixture.Server.Definition);

        // The jump moved the authority off the value the observer was last sent, and the observer followed it.
        Assert.NotEqual(actual: authority, expected: written);
        Assert.Equal(expected: authority, actual: Read(document: hold.Definition!));
    }
}
