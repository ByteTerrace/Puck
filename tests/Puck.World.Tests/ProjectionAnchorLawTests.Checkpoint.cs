using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class ProjectionAnchorLawTests {
    [InlineData("advance")]
    [InlineData("cycle")]
    [InlineData("dynamics")]
    [Theory]
    public async Task A_checkpoint_restore_delivers_changed_observations_without_a_keyed_clock(string trait) {
        var density = new BindableScalar(binding: $"state.{Clock}");
        var row = trait switch {
            "advance" => Row(advance: PerTick(raw: 37L), raw: 0L),
            "cycle" => Row(cycle: new StateCycle(Output: CycleOutput.Turns, TicksPerStep: 3L), raw: 0L),
            _ => Row(dynamics: new StateDynamics(Row: "chase"), raw: 16384L),
        };
        var definition = Document(row: row) with {
            RenderRaw = new WorldRenderDefaults(Atmosphere: new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: density))),
            TimelineRaw = null,
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var sink = new WorldFederationProjectionSink(
            authority: "boot",
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 1f)),
            revision: static () => 1,
            server: fixture.Server,
            tier: WorldDisclosureTier.Presentation
        );
        using var lease = fixture.Server.AttachSink(sink: sink);

        for (var index = 0; (index < 4); index++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: WorldAuthorityHostRowCheckpoint.Empty, reason: out var reason), userMessage: reason);
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: Principal.Console, Row: Clock, Key: WorldStateRow.SlotKey.Value, Value: 458752L, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();
        fixture.Server.RestoreCheckpoint(checkpoint: checkpoint!);
        fixture.Step();
        Assert.Null(@object: sink.DetachReason);

        using var wire = new MemoryStream();

        await sink.StreamAsync(ct: TestContext.Current.CancellationToken, output: wire);
        sink.Release();
        using var received = new MemoryStream(buffer: wire.ToArray());
        var hold = new WorldProjectionHold();
        WorldDefinition? stale = null;
        var deltas = 0;

        while (received.Position < received.Length) {
            var frame = await WorldFederationCodec.ReadResponseAsync(ct: TestContext.Current.CancellationToken, stream: received);

            Assert.True(condition: frame.Ok);

            if (frame.Kind == ((byte)WorldFederationResponse.Definition)) {
                Assert.True(condition: WorldFederationCodec.TryDecodeDocument(body: frame.Body.Span, definition: out _, failure: out var failure, hold: hold, tier: out _, version: out _), userMessage: failure.ToString());
            } else if (frame.Kind == ((byte)WorldFederationResponse.ProjectionDelta)) {
                Assert.True(condition: WorldFederationCodec.TryDecodeProjectionDelta(body: frame.Body.Span, definition: out var delivered, failure: out var failure, hold: hold, stamp: out _, valuesOnly: out _, version: out _), userMessage: failure.ToString());
                stale ??= delivered;
                deltas++;
            }
        }

        long Read(WorldDefinition document) {
            var time = fixture.Server.Time;

            Assert.True(condition: WorldStateReader.TryReadEased(definition: document, engineTick: time.EngineTick, key: null, rawValue: out var raw, row: out _, rowName: Clock, text: out _, tick: time.Tick));
            return raw!.Value;
        }

        Assert.NotNull(@object: stale);
        Assert.NotNull(@object: hold.Definition);
        Assert.Null(@object: AnchorOf(definition: hold.Definition));
        var expected = Read(document: fixture.Server.Definition);

        Assert.NotEqual(expected: expected, actual: Read(document: stale));
        Assert.Equal(expected: expected, actual: Read(document: hold.Definition));
        Assert.Equal(actual: deltas, expected: 2);
    }
}
