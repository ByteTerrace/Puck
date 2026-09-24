using System.Reflection;
using Puck.Transpiler.Ast;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The one door: <see cref="WorldCompiler"/> is the only way into <c>puck.world.definition.v1</c>
/// lowering from outside <c>Puck.World.Transpiler</c>, so <c>puck compile</c>, the game's boot path and every
/// harness run the same stages in the same order.</summary>
public class OneDoorTests {
    [Fact]
    public void TheEmitterPublishesNoSecondWayIntoTheLowering() {
        var doors = typeof(WorldDocumentEmitter)
            .GetMethods(bindingAttr: BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Static)
            .Where(predicate: static method => method.GetParameters().Any(predicate: static parameter => (parameter.ParameterType == typeof(DocumentNode))))
            .Select(selector: static method => method.Name)
            .Order(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: doors
            ),
            expected: ""
        );
    }
    [Fact]
    public void TheDoorCompilesASourceToItsDocument() {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: "schema: \"puck.world.definition.v1\"\ndocumentId: \"door\"\n"
        );

        Assert.Equal(
            actual: compilation.RequireJson()["documentId"]?.ToString(),
            expected: "door"
        );
    }
}
