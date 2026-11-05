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
        var result = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: Layout(member: "secondz"));
        var finding = Assert.Single(collection: result.Diagnostics, predicate: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.SemanticValidation));

        Assert.Contains(expectedSubstring: "Unknown member 'secondz'; no document member consumes it; nearest: 'seconds'.", actualString: finding.Message);
    }
    [Fact]
    public void AnUnknownMemberFarFromEveryDeclaredMemberSuggestsNothing() {
        var result = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: Layout(member: "bogusMember"));
        var finding = Assert.Single(collection: result.Diagnostics, predicate: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.SemanticValidation));

        Assert.Contains(expectedSubstring: "Unknown member 'bogusMember'; no document member consumes it.", actualString: finding.Message);
    }
}
