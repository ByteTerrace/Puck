using Puck.Cli.Determinism;
using Xunit;

namespace Puck.Cli.Runs.Tests;

public sealed class DeterminismAttestationLawTests {
    private const string Pin = "sha256-64/0123456789abcdef";

    [InlineData("")]
    [InlineData("document unexpected abc\n")]
    [InlineData("document fingerprint abc\ndocument definition abc\ndocument catalog abc\ndocument unexpected abc\n")]
    [Theory]
    public void StreamsWithoutTheDocumentAttestationAreRefused(string documents) {
        var text = $"{DeterminismStream.Version}\nmanifest {Pin}\ncomponents {string.Join(separator: ' ', values: DeterminismStream.Components)}\nscenario body 1\n{documents}tick 1 {string.Join(separator: ' ', values: Enumerable.Repeat("0000000000000000", DeterminismStream.Components.Count))}\nend\n";

        Assert.False(condition: DeterminismStream.TryParse(error: out var error, stream: out _, text: text));
        Assert.Contains(actualString: error, expectedSubstring: "document");
    }
}
