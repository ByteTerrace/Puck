using System.Text;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Malformed channel objects encountered during import rewriting remain named composition refusals.</summary>
public sealed class ChannelCompositionRefusalTests {
    [Fact]
    public void AFragmentWithAMalformedChannelRefusesInsteadOfThrowingDuringAliasRewriting() {
        var fragment = Encoding.UTF8.GetBytes(s: """
            {"rules":[{"name":"probe","effects":[
                {"$type":"setState","state":{"channel":true},"value":1}
            ]}]}
            """);

        Assert.False(condition: WorldDefinitionFileSource.TryComposeFragmentBytes(
            hostBytes: Encoding.UTF8.GetBytes(s: "{}"),
            fragmentBytes: fragment,
            alias: "sample",
            composed: out var composed,
            reason: out var reason
        ));
        Assert.Null(@object: composed);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "channel");
    }
}
