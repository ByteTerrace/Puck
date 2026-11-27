using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>An unknown document member a typed row refuses is refused with the declared names nearest it, the same
/// suggestion <see cref="WorldPayloadRefusal"/> gives a payload member.</summary>
public sealed class UnknownMemberSuggestionLawTests {
    private static string Layout(string member) => $$"""
        views {
          layout a {
            seatCount: 1
            slots [ { camera: null } ]
            transition {
              {{member}}: 1
            }
          }
        }
        """;

    [Fact]
    public void AnUnknownMemberNamesTheDeclaredMemberNearestIt() {
        var result = WorldCompileSession.Shared.Compile(cancellationToken: TestContext.Current.CancellationToken, source: Layout(member: "secondz"));
        var finding = Assert.Single(collection: result.Diagnostics, predicate: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.UnknownMember));

        Assert.Contains(expectedSubstring: "'secondz' is not a member of", actualString: finding.Message);
        Assert.Contains(expectedSubstring: "Did you mean 'seconds'?", actualString: finding.Message);
        Assert.Contains(expectedSubstring: "may be older than the document", actualString: finding.Message);
    }
    [Fact]
    public void AnUnknownMemberFarFromEveryDeclaredMemberSuggestsNothing() {
        var result = WorldCompileSession.Shared.Compile(cancellationToken: TestContext.Current.CancellationToken, source: Layout(member: "bogusMember"));
        var finding = Assert.Single(collection: result.Diagnostics, predicate: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.UnknownMember));

        Assert.Contains(expectedSubstring: "'bogusMember' is not a member of", actualString: finding.Message);
        Assert.DoesNotContain(expectedSubstring: "Did you mean", actualString: finding.Message);
        Assert.Contains(expectedSubstring: "It takes 'seconds'", actualString: finding.Message);
    }
}
