using Puck.Cli.Analysis;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Source.Tests;

public sealed class DeclarationOwnershipLawTests {
    [Fact]
    public void TypeInventoryCannotChooseAnOwnerFromACommentOrString() {
        using var files = new TemporaryDirectory();
        var source = files.WriteText(name: "types.cs", text: """
            // class FakeLawTests {}
            class RealLawTests {
                string text = "record FakeRecord {}";
                class NestedLawTests {}
            }
            record ActualRecord;
            """);
        var declarations = DeclarationsCommand.ReadTypes(files: [source], relativeTo: files.RootPath);

        Assert.Equal(expected: new[] { "RealLawTests", "NestedLawTests", "ActualRecord" }, actual: declarations.Select(selector: static type => type.Name));
        Assert.All(collection: declarations, action: static type => Assert.Equal(actual: type.Path, expected: "types.cs"));
    }
}
