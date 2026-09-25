using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for the planner's dispatch shapes and package storage. An indirect dispatch reads a buffer version in the
/// indirect-argument state, which orders the version's writer first and moves the buffer out of any other read state
/// through a barrier; a structured or counted buffer resolves its capacity from the host's counts. The pipeline node
/// records only extent dispatches over raw fixed buffers, so the planner refuses the other shapes and storages on a
/// shader pass by name, and admits them on a package pass.
/// </summary>
public sealed class ShaderPipelineDispatchLawTests {
    private static ShaderPipelineResource Words(string name, ulong sizeBytes = 16, uint? stride = null, ShaderPipelineBufferCount? count = null, ShaderPipelineInitialization initialization = ShaderPipelineInitialization.Undefined) => new(
        Count: count,
        Initialization: initialization,
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: name,
        SizeBytes: ((count is null)
            ? sizeBytes
            : null),
        StrideBytes: stride
    );
    private static ShaderPipelinePass Pass(string name, ShaderPipelineDocumentPassKind kind, ResourceReference[] inputs, ResourceReference[] outputs, ShaderPipelineDispatch? dispatch = null) => new(
        Dispatch: dispatch,
        EntryPoint: "main",
        Inputs: inputs,
        Kind: kind,
        Name: name,
        Outputs: outputs,
        Source: $"{name}.hlsl"
    );
    private static ShaderPipelinePackagePass Package(string name, ResourceReference[] inputs, ResourceReference[] outputs, ShaderPipelineDispatch? dispatch = null) => new(
        Dispatch: dispatch,
        Inputs: inputs,
        Name: name,
        Outputs: outputs,
        Package: "test.package"
    );
    // args and a counted table are written by "count"; args is read as shader data by "peek", then as dispatch
    // arguments by "consume", which also reads the table. The passes are declared in reverse, so the order is the
    // planner's.
    private static ShaderPipelinePlan PlanIndirect() => new ShaderPipelineCompiler().Compile(
        definition: new ShaderPipelineDefinition(
            name: "indirect",
            outputs: ["result"],
            passes: [],
            resources: [
                Words(name: "args"),
                Words(name: "peeked"),
                Words(name: "result"),
                Words(
                    count: new ShaderPipelineBufferCount(
                        Basis: ShaderPipelineCountBasis.Instances,
                        Elements: 2
                    ),
                    name: "table",
                    stride: 16
                ),
            ]
        ),
        packages: [
            Package(
                dispatch: ShaderPipelineDispatch.Indirect(arguments: "args"),
                inputs: ["peeked", "table"],
                name: "consume",
                outputs: ["result"]
            ),
            Package(
                dispatch: ShaderPipelineDispatch.Groups(x: 1),
                inputs: ["args"],
                name: "peek",
                outputs: ["peeked"]
            ),
            Package(
                inputs: [],
                name: "count",
                outputs: ["args", "table"]
            ),
        ]
    );
    private static void AssertRefused(string code, ShaderPipelineResource[] resources, string[] outputs, ShaderPipelinePass[]? passes = null, ShaderPipelinePackagePass[]? packages = null) {
        var exception = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(
            definition: new ShaderPipelineDefinition(
                name: "refused",
                outputs: outputs,
                passes: (passes ?? []),
                resources: resources
            ),
            packages: (packages ?? [])
        ));

        Assert.Contains(
            collection: exception.Diagnostics,
            filter: diagnostic => (diagnostic.Code == code)
        );
    }

    [Fact]
    public void AnIndirectDispatchReadsItsArgumentsAfterTheirWriterInTheIndirectArgumentState() {
        var plan = PlanIndirect();

        Assert.Equal(
            actual: plan.PassOrder,
            expected: ["count", "peek", "consume"]
        );

        var consume = plan.Passes[2];
        var arguments = consume.Accesses[0];

        Assert.Equal(
            actual: arguments.Version,
            expected: "args"
        );
        Assert.Equal(
            actual: arguments.Use,
            expected: new ShaderPipelineAccessState(
                Access: GpuAccess.IndirectCommandRead,
                Layout: GpuImageLayout.Undefined,
                Stage: GpuStage.DrawIndirect
            )
        );
        // A shader read already made the write visible, but the arguments are another read state, so the planner
        // records the move between them.
        Assert.Equal(
            actual: arguments.Barrier,
            expected: new ShaderPipelineBarrier(
                DestinationAccess: GpuAccess.IndirectCommandRead,
                DestinationStage: GpuStage.DrawIndirect,
                Kind: ShaderPipelineBarrierKind.Buffer,
                NewLayout: GpuImageLayout.Undefined,
                OldLayout: GpuImageLayout.Undefined,
                SourceAccess: GpuAccess.ShaderRead,
                SourceStage: GpuStage.ComputeShader
            )
        );
        Assert.Equal(
            actual: arguments.PriorPass,
            expected: 1
        );
    }
    [Fact]
    public void TheVocabularyReadsFromADocument() {
        var resource = System.Text.Json.JsonSerializer.Deserialize(
            json: """{ "name": "table", "kind": "Buffer", "strideBytes": 16, "count": { "basis": "Instances", "elements": 2 } }""",
            jsonTypeInfo: ShaderPipelineJsonContext.Default.ShaderPipelineResource
        );
        var pass = System.Text.Json.JsonSerializer.Deserialize(
            json: """{ "name": "consume", "source": "p", "entryPoint": "", "kind": "Compute", "dispatch": { "kind": "Indirect", "arguments": "args", "argumentsOffsetBytes": 12 } }""",
            jsonTypeInfo: ShaderPipelineJsonContext.Default.ShaderPipelinePass
        );

        Assert.Equal(
            actual: resource,
            expected: Words(
                count: new ShaderPipelineBufferCount(
                    Basis: ShaderPipelineCountBasis.Instances,
                    Elements: 2
                ),
                name: "table",
                stride: 16
            )
        );
        Assert.Equal(
            actual: pass!.Dispatch,
            expected: ShaderPipelineDispatch.Indirect(
                arguments: "args",
                offsetBytes: 12
            )
        );
    }
    [Fact]
    public void AReadOfTheSameKindAfterReadsRecordsNothing() {
        var reads = new ShaderPipelineAccessState(
            Access: GpuAccess.ShaderRead,
            Layout: GpuImageLayout.Undefined,
            Stage: GpuStage.ComputeShader
        );

        Assert.False(condition: ShaderPipelineAccessState.Fresh.ChangesReadState(use: reads));
        Assert.False(condition: reads.ChangesReadState(use: reads));
        Assert.Equal(
            actual: ShaderPipelineBarrier.Between(
                kind: ShaderPipelineResourceKind.Buffer,
                prior: reads,
                use: reads
            ).Kind,
            expected: ShaderPipelineBarrierKind.None
        );
    }
    [Fact]
    public void ACountedBufferResolvesItsCapacityFromTheHostsCounts() {
        var counts = new ShaderPipelineStorageCounts(
            Height: 4,
            Instances: 7,
            ProgramWords: 100,
            Width: 8
        );

        Assert.Equal(
            actual: Words(
                count: new ShaderPipelineBufferCount(
                    Basis: ShaderPipelineCountBasis.Instances,
                    Elements: 2
                ),
                name: "a",
                stride: 16
            ).ResolveSizeBytes(counts: counts),
            expected: ((16UL * 2UL) * 7UL)
        );
        Assert.Equal(
            actual: Words(
                count: new ShaderPipelineBufferCount(Basis: ShaderPipelineCountBasis.Extent),
                name: "b",
                stride: 80
            ).ResolveSizeBytes(counts: counts),
            expected: (80UL * 32UL)
        );
        Assert.Equal(
            actual: Words(
                count: new ShaderPipelineBufferCount(Basis: ShaderPipelineCountBasis.ProgramWords),
                name: "c"
            ).ResolveSizeBytes(counts: counts),
            expected: 400UL
        );
        Assert.Equal(
            actual: Words(
                name: "d",
                sizeBytes: 24
            ).ResolveSizeBytes(counts: counts),
            expected: 24UL
        );
        Assert.Equal(
            actual: PlanIndirect().FindResource(name: "table")!.Declaration.ResolveSizeBytes(counts: counts),
            expected: 224UL
        );
    }
    [InlineData(ShaderPipelineCountBasis.Extent, 0U, 4U, 7UL, 100UL)]
    [InlineData(ShaderPipelineCountBasis.Extent, 8U, 0U, 7UL, 100UL)]
    [InlineData(ShaderPipelineCountBasis.Instances, 8U, 4U, 0UL, 100UL)]
    [InlineData(ShaderPipelineCountBasis.ProgramWords, 8U, 4U, 7UL, 0UL)]
    [Theory]
    public void ACountedBufferIsRefusedCountsHoldingNoneOfItsBasis(ShaderPipelineCountBasis basis, uint width, uint height, ulong instances, ulong programWords) {
        var buffer = Words(
            count: new ShaderPipelineBufferCount(Basis: basis),
            name: "empty"
        );
        var counts = new ShaderPipelineStorageCounts(
            Height: height,
            Instances: instances,
            ProgramWords: programWords,
            Width: width
        );

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => buffer.ResolveSizeBytes(counts: counts));

        Assert.Contains(
            expectedSubstring: $"Buffer 'empty' counts by {basis}",
            actualString: refusal.Message
        );
    }
    [Fact]
    public void AShaderPassIsRefusedEveryShapeAndStorageThePipelineNodeCannotRecord() {
        AssertRefused(
            code: "SHADERPIPE_DISPATCH_PACKAGE",
            outputs: ["out"],
            passes: [Pass(dispatch: ShaderPipelineDispatch.Groups(x: 2), inputs: [], kind: ShaderPipelineDocumentPassKind.Compute, name: "grid", outputs: ["out"])],
            resources: [Words(name: "out")]
        );
        AssertRefused(
            code: "SHADERPIPE_DISPATCH_PACKAGE",
            outputs: ["out"],
            passes: [
                Pass(inputs: [], kind: ShaderPipelineDocumentPassKind.Compute, name: "write", outputs: ["args"]),
                Pass(dispatch: ShaderPipelineDispatch.Indirect(arguments: "args"), inputs: [], kind: ShaderPipelineDocumentPassKind.Compute, name: "read", outputs: ["out"]),
            ],
            resources: [Words(name: "args"), Words(name: "out")]
        );
        AssertRefused(
            code: "SHADERPIPE_PACKAGE_STORAGE",
            outputs: ["out"],
            passes: [Pass(inputs: [], kind: ShaderPipelineDocumentPassKind.Compute, name: "write", outputs: ["out"])],
            resources: [Words(name: "out", stride: 16)]
        );
        AssertRefused(
            code: "SHADERPIPE_PACKAGE_STORAGE",
            outputs: ["out"],
            packages: [Package(inputs: [], name: "write", outputs: ["out"])],
            resources: [Words(count: new ShaderPipelineBufferCount(Basis: ShaderPipelineCountBasis.Instances), name: "out")]
        );
    }
    [Fact]
    public void AMalformedDispatchOrBufferLayoutIsRefusedByName() {
        ShaderPipelinePackagePass Consume(ShaderPipelineDispatch dispatch, ResourceReference[]? inputs = null) => Package(
            dispatch: dispatch,
            inputs: (inputs ?? []),
            name: "consume",
            outputs: ["out"]
        );
        var writer = Package(inputs: [], name: "write", outputs: ["args"]);
        ShaderPipelineResource[] resources = [Words(name: "args"), Words(name: "out")];

        AssertRefused(code: "SHADERPIPE_DISPATCH_ARGUMENTS", outputs: ["out"], packages: [writer, Consume(dispatch: ShaderPipelineDispatch.Indirect(arguments: "args", offsetBytes: 6))], resources: resources);
        AssertRefused(code: "SHADERPIPE_DISPATCH_ARGUMENTS", outputs: ["out"], packages: [writer, Consume(dispatch: ShaderPipelineDispatch.Indirect(arguments: "args", offsetBytes: 8))], resources: resources);
        AssertRefused(code: "SHADERPIPE_DISPATCH_ARGUMENTS", outputs: ["out"], packages: [writer, Consume(dispatch: ShaderPipelineDispatch.Indirect(arguments: "args"), inputs: ["args"])], resources: resources);
        AssertRefused(code: "SHADERPIPE_UNKNOWN_RESOURCE", outputs: ["out"], packages: [writer, Consume(dispatch: ShaderPipelineDispatch.Indirect(arguments: "missing"))], resources: resources);
        AssertRefused(code: "SHADERPIPE_DISPATCH_SHAPE", outputs: ["out"], packages: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 0))], resources: resources);
        AssertRefused(code: "SHADERPIPE_DISPATCH_SHAPE", outputs: ["out"], packages: [writer, Consume(dispatch: new ShaderPipelineDispatch(Arguments: "args", Kind: ShaderPipelineDispatchKind.Extent))], resources: resources);
        AssertRefused(code: "SHADERPIPE_BUFFER_STRIDE", outputs: ["out"], packages: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 1))], resources: [Words(name: "args"), Words(name: "out", sizeBytes: 24, stride: 16)]);
        AssertRefused(code: "SHADERPIPE_BUFFER_STRIDE", outputs: ["out"], packages: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 1))], resources: [Words(name: "args"), Words(name: "out", stride: 6)]);
        AssertRefused(code: "SHADERPIPE_BUFFER_COUNT", outputs: ["out"], packages: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 1))], resources: [Words(name: "args"), Words(count: new ShaderPipelineBufferCount(Basis: ShaderPipelineCountBasis.Instances, Elements: 0), name: "out")]);
        AssertRefused(code: "SHADERPIPE_BUFFER_SIZE", outputs: ["out"], packages: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 1))], resources: [Words(name: "args"), Words(name: "out") with { Count = new ShaderPipelineBufferCount(Basis: ShaderPipelineCountBasis.Instances) }]);
    }
}
