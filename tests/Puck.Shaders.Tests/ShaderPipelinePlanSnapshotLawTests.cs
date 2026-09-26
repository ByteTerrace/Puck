using System.Text.Json;

namespace Puck.Shaders.Tests;

/// <summary>
/// The law of a plan's definition: <see cref="ShaderPipelinePlan.Definition"/> is the document the plan was made from,
/// package passes included, each with its ports and its config, alongside the shader passes, the tick rate and the
/// tiers; and it is a snapshot, holding none of the source document's lists.
/// </summary>
public sealed class ShaderPipelinePlanSnapshotLawTests {
    private static ShaderPipelineResource Image(string name, bool external = false) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: "R8G8B8A8Unorm",
        Initialization: (external
            ? ShaderPipelineInitialization.External
            : ShaderPipelineInitialization.Undefined),
        Name: name
    );
    // The host's world, toned by a compute shader pass, grained by a configured post pass, then drawn over by the overlay.
    private static RenderGraphDefinition Chain() => new(
        Name: "chain",
        Outputs: ["composed"],
        Packages: [
            new RenderGraphPackagePass(
                Config: JsonDocument.Parse(json: """{"intensity":0.25}""").RootElement,
                Inputs: ["toned"],
                Name: "grain",
                Outputs: ["grained"],
                Package: RenderGraphPackageCatalog.SdfFilmGrain
            ),
            new RenderGraphPackagePass(
                Inputs: ["grained"],
                Name: "overlay",
                Outputs: ["composed"],
                Package: RenderGraphPackageCatalog.Overlay
            ),
        ],
        Passes: [new ShaderPipelinePass(
            EntryPoint: "main",
            Inputs: [new ResourceReference(Name: "world")],
            Kind: ShaderPipelineDocumentPassKind.Compute,
            Name: "tone",
            Outputs: [new ResourceReference(Name: "toned")],
            Source: "tone.hlsl"
        )],
        Resources: [
            Image(name: "composed"),
            Image(name: "grained"),
            Image(name: "toned"),
            Image(
                external: true,
                name: "world"
            ),
        ],
        Schema: RenderGraphSchemas.Graph
    );

    [Fact]
    public void APlansDefinitionCarriesItsPackagePassesAsASnapshot() {
        var source = Chain();
        var plan = new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: source).Pipeline;
        var packages = plan.Definition.Packages;

        Assert.NotNull(@object: packages);
        Assert.NotSame(
            actual: packages,
            expected: source.Packages
        );
        Assert.Equal(
            actual: packages.Select(selector: static package => (package.Name, package.Package, string.Join(separator: ",", values: package.InputReferences.Select(selector: static input => input.Name)), string.Join(separator: ",", values: package.OutputReferences.Select(selector: static output => output.Name)))),
            expected: [
                ("grain", RenderGraphPackageCatalog.SdfFilmGrain, "toned", "grained"),
                ("overlay", RenderGraphPackageCatalog.Overlay, "grained", "composed"),
            ]
        );

        for (var index = 0; (index < packages.Count); index++) {
            Assert.NotSame(
                actual: packages[index].Inputs,
                expected: source.Packages![index].Inputs
            );
            Assert.NotSame(
                actual: packages[index].Outputs,
                expected: source.Packages[index].Outputs
            );
        }

        Assert.Equal(
            actual: packages[0].Config?.GetRawText(),
            expected: """{"intensity":0.25}"""
        );
        Assert.Null(@object: packages[1].Config);
    }
}
