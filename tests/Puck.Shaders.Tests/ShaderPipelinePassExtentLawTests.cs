using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for the one pass-extent rule: the planner gives every planned pass, whatever its kind, the dimensions of its first
/// output whose storage declares them, else of its first input whose storage does, else none, so it runs at the frame's
/// extent; <see cref="ShaderPipelinePlannedPass.ResolveExtent"/> resolves that against a frame, and a resized frame
/// resolves again through the same rule.
/// </summary>
public sealed class ShaderPipelinePassExtentLawTests {
    private static ShaderPipelineResource Image(string name, ShaderPipelineDimensions dimensions) => new(
        Dimensions: dimensions,
        Format: "R8G8B8A8Unorm",
        Name: name
    );
    private static ShaderPipelineResource Buffer(string name) => new(
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: name,
        SizeBytes: 64UL
    );
    private static ShaderPipelinePass Pass(string name, ShaderPipelineDocumentPassKind kind, IReadOnlyList<ResourceReference> inputs, IReadOnlyList<ResourceReference> outputs) => new(
        name,
        $"{name}.hlsl",
        "main",
        kind,
        inputs,
        outputs
    );

    // A package writing a quarter-size scene, a compute pass reading it into a half-size image, a fullscreen pass drawing
    // that into a fixed 16x8, a compute pass counting the fixed image into a buffer (its output declares no dimensions,
    // so its input's rule), and a compute pass reducing the buffer (nothing declares any, so the frame's).
    internal static ShaderPipelinePlan Plan() => new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: new RenderGraphDefinition(
        name: "extents",
        outputs: ["fixed", "sum"],
        packages: [new RenderGraphPackagePass(
            Name: "world",
            Outputs: ["scene"],
            Package: RenderGraphPackageCatalog.SdfWorld
        )],
        passes: [
            Pass(inputs: ["scene"], kind: ShaderPipelineDocumentPassKind.Compute, name: "fill", outputs: ["half"]),
            Pass(inputs: ["half"], kind: ShaderPipelineDocumentPassKind.Fullscreen, name: "blit", outputs: ["fixed"]),
            Pass(inputs: ["fixed"], kind: ShaderPipelineDocumentPassKind.Compute, name: "count", outputs: ["counts"]),
            Pass(inputs: ["counts"], kind: ShaderPipelineDocumentPassKind.Compute, name: "total", outputs: ["sum"]),
        ],
        resources: [
            Image(dimensions: ShaderPipelineDimensions.Relative(height: 0.25, width: 0.25), name: "scene"),
            Image(dimensions: ShaderPipelineDimensions.Relative(height: 0.5, width: 0.5), name: "half"),
            Image(dimensions: ShaderPipelineDimensions.Absolute(height: 8, width: 16), name: "fixed"),
            Buffer(name: "counts"),
            Buffer(name: "sum"),
        ]
    )).Pipeline;

    [InlineData(64u, 32u)]
    [InlineData(128u, 96u)]
    [Theory]
    public void EveryPassKindResolvesItsExtentThroughTheOneRule(uint width, uint height) {
        var plan = Plan();

        Assert.Equal(
            actual: plan.Passes.ToDictionary(
                elementSelector: pass => pass.ResolveExtent(
                    frameHeight: height,
                    frameWidth: width
                ),
                keySelector: static pass => pass.Name
            ),
            expected: new Dictionary<string, (uint Width, uint Height)> {
                ["blit"] = (16u, 8u),
                ["count"] = (16u, 8u),
                ["fill"] = ((width / 2u), (height / 2u)),
                ["total"] = (width, height),
                ["world"] = ((width / 4u), (height / 4u)),
            }
        );
    }
    [Fact]
    public void ThePlannerStatesEachPassesExtentSourceOnce() {
        var plan = Plan();

        Assert.Equal(
            actual: plan.Passes.ToDictionary(
                elementSelector: static pass => pass.Extent,
                keySelector: static pass => pass.Name
            ),
            expected: new Dictionary<string, ShaderPipelineDimensions?> {
                ["blit"] = ShaderPipelineDimensions.Absolute(height: 8, width: 16),
                ["count"] = ShaderPipelineDimensions.Absolute(height: 8, width: 16),
                ["fill"] = ShaderPipelineDimensions.Relative(height: 0.5, width: 0.5),
                ["total"] = null,
                ["world"] = ShaderPipelineDimensions.Relative(height: 0.25, width: 0.25),
            }
        );
    }
}
