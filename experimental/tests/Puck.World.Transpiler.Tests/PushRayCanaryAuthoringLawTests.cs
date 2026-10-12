using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The shipped push-ray source must retain typed lexical pool fields through authoring and engine admission.</summary>
public sealed class PushRayCanaryAuthoringLawTests {
    [Fact]
    public void ShippedPushRaySourceCompilesThroughWorldAdmission() {
        var path = RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Canaries/push-ray/fixture.puck");
        var source = File.ReadAllText(path: path);
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourcePath: path
        );

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(source));
        var document = compilation.RequireJson();

        Assert.True(
            condition: PuckDocumentComposer.TryComposeWorldDocument(
                rootResolvedPath: path,
                rootBytes: CanonicalJsonDocument.Serialize(node: document),
                composed: out var composed,
                chainBytes: out _,
                reason: out var reason
            ),
            userMessage: reason
        );
        _ = WorldDefinitionSerialization.Deserialize(utf8Json: CanonicalJsonDocument.Serialize(node: (composed ?? document)));
    }

}
