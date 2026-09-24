using Puck.State;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: a family's size and its member indices are read exactly and bounded by the row
/// ceiling before anything is narrowed or expanded. A size or index that is not an integer, that lies outside the
/// ceiling, or that would wrap a 32-bit counter is a diagnostic; none is brought inside the range, and no range is
/// walked to an end its counter cannot reach.</summary>
public sealed class FamilyAdmissionLawTests {
    private static DiagnosticBag Diagnose(string declaration) => WorldCompiler.Compile(
        cancellationToken: TestContext.Current.CancellationToken,
        source: $"schema: \"puck.world.definition.v1\"\n\nstate {{\n    world {{\n        {declaration}\n    }}\n}}\n"
    ).Diagnostics;

    [InlineData("table scores[4294967297]")]
    [InlineData("table scores[9223372036854775807]")]
    [InlineData("table scores[0]")]
    [InlineData("table scores[-1]")]
    [InlineData("table scores[2.5]")]
    [Theory]
    public void ASizeThatIsNotAnIntegerInsideTheRowCeilingIsRefused(string declaration) => Assert.Contains(
        collection: Diagnose(declaration: declaration),
        filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.FamilySizeInvalid)
    );
    [Fact]
    public void ASizeOnePastTheRowCeilingIsRefusedAndTheCeilingItselfExpands() {
        Assert.Contains(
            collection: Diagnose(declaration: $"table scores[{(StateCapacity.MaxRows + 1)}]"),
            filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.FamilySizeInvalid)
        );
        Assert.DoesNotContain(
            collection: Diagnose(declaration: $"table scores[{StateCapacity.MaxRows}]"),
            filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.FamilySizeInvalid)
        );
    }
    [InlineData("slot pile[2147483647..2147483647] = 0")]
    [InlineData("slot pile[4294967296..4294967297] = 0")]
    [InlineData("slot pile[-1..2] = 0")]
    [InlineData("slot pile[0..99999] = 0")]
    [InlineData("slot pile[1.5..2] = 0")]
    [Theory]
    public void AMemberIndexOutsideTheRowCeilingIsRefusedAsWritten(string declaration) => Assert.Contains(
        collection: Diagnose(declaration: declaration),
        filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.FamilyMembersInvalid)
    );
    [Fact]
    public void ARangeRepeatingAnIndexIsRefusedAndAGappedOneExpands() {
        Assert.Contains(
            collection: Diagnose(declaration: "slot pile[0..3, 2..5] = 0"),
            filter: static diagnostic => ((diagnostic.Code == PuckDiagnosticCodes.FamilyMembersInvalid) && diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "twice"))
        );
        Assert.DoesNotContain(
            collection: Diagnose(declaration: "slot pile[0..3, 8..9] = 0"),
            filter: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)
        );
    }
}
