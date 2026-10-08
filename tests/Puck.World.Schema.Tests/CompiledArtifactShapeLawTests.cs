using Puck.Assets;
using Puck.Testing;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Laws for the shape fingerprint a compiled world and a bake pack carry after their format version: each
/// artifact names the shape it was written under, and a reader of the same magic and version refuses any other shape by
/// that fingerprint's name, before the header or any chunk is read.</summary>
public sealed class CompiledArtifactShapeLawTests {
    // The shared container's header: the four-byte magic, the version as one variable-width byte, then the fingerprint.
    private const int FingerprintOffset = (ChunkContainer.MagicLength + 1);
    private const int FingerprintLength = 16;

    private static byte[] CompiledWorldBytes() => CompiledWorld.Encode(
        chunks: [],
        header: new CompiledWorldHeader(
            CatalogFingerprint: "catalog",
            DefinitionHash: new AssetContentHash(Value: 1UL),
            EngineBuild: "build",
            InstanceIdentity: "instance"
        )
    );
    private static string FingerprintOf(byte[] artifact) => System.Text.Encoding.ASCII.GetString(bytes: artifact.AsSpan(
        length: FingerprintLength,
        start: FingerprintOffset
    ));
    private static string Reshape(byte[] artifact) {
        var expected = FingerprintOf(artifact: artifact);
        var other = ((expected[0] == '0') ? ('1' + expected[1..]) : ('0' + expected[1..]));

        System.Text.Encoding.ASCII.GetBytes(
            bytes: artifact.AsSpan(start: FingerprintOffset),
            chars: other
        );

        return other;
    }

    [Fact]
    public void ACompiledWorldCarriesTheShapeTheLedgerRecords() {
        Assert.Equal(
            actual: FingerprintOf(artifact: CompiledWorldBytes()),
            expected: FormatLedgerShapes.Of(id: "CompiledWorld.FormatVersion")
        );
    }
    [Fact]
    public void ACompiledWorldOfTheSameVersionAndAnotherShapeIsRefusedByItsFingerprint() {
        var bytes = CompiledWorldBytes();
        var recorded = FingerprintOf(artifact: bytes);
        var other = Reshape(artifact: bytes);

        Assert.NotEqual(
            actual: other,
            expected: recorded
        );
        Assert.False(condition: CompiledWorld.TryDecode(
            container: out var container,
            content: bytes,
            header: out var header,
            reason: out var reason
        ));
        Assert.Null(@object: container);
        Assert.Null(@object: header);
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"another shape than {recorded}"
        );
    }
    [Fact]
    public void ABakePackCarriesTheShapeTheLedgerRecords() {
        Assert.Equal(
            actual: FingerprintOf(artifact: WorldBakePack.Encode(outcomes: [])),
            expected: FormatLedgerShapes.Of(id: "WorldBakePack.FormatVersion")
        );
    }
    [Fact]
    public void ABakePackOfTheSameVersionAndAnotherShapeIsRefusedByItsFingerprint() {
        var bytes = WorldBakePack.Encode(outcomes: []);
        var recorded = FingerprintOf(artifact: bytes);

        _ = Reshape(artifact: bytes);
        Assert.False(condition: WorldBakePack.TryDecode(
            content: bytes,
            pack: out var pack,
            reason: out var reason
        ));
        Assert.Null(@object: pack);
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"another shape than {recorded}"
        );
    }
}
