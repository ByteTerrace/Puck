using Puck.Testing;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A checkpoint embeds its definition with the relative asset paths its document authored, so a state is
/// decodable only beside the directory those paths resolve against. The release's reset decision decodes the source's
/// state as the silo that would restore it does, with that directory, and so never discards a state the silo can
/// restore; a state that does not decode there still resets, with its reason.</summary>
public sealed class WorldReleaseStateResetDirectoryLawTests {
    // One world's export from a document loaded from a file that names its patch beside itself.
    private static Dictionary<string, ReadOnlyMemory<byte>> ExportBeside(TemporaryDirectory directory) {
        CompiledWorldFixtures.WritePatchBeside(directory: directory);
        var path = directory.WriteText(
            name: "beside.world.json",
            text: CompiledWorldFixtures.World.Replace(
                newValue: CompiledWorldFixtures.PatchBeside,
                oldValue: "PATCH"
            )
        );

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(definition: out var loaded, path: path, reason: out var loadReason),
            userMessage: loadReason
        );
        using var fixture = Fixtures.FreshServer(definition: loaded!);

        fixture.Step();
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                WorldAuthorityHostRowCheckpoint.Empty,
                out var checkpoint,
                out var reason
            ),
            userMessage: reason
        );
        return new() { ["row"] = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!) };
    }

    [Fact]
    public void AStateTheSiloCanRestoreIsNeverReset() {
        using var directory = new TemporaryDirectory();
        var export = ExportBeside(directory: directory);

        // The decode the silo runs to restore the state, beside its document directory.
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: export["row"].Span,
                checkpoint: out _,
                documentDirectory: directory.RootPath,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Null(@object: WorldReleaseCoordinator.FindStateReset(
            checkpoints: export,
            documentDirectory: directory.RootPath
        ));
    }
}
