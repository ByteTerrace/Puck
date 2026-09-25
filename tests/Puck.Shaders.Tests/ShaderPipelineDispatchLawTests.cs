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
    // A count of one term.
    private static ShaderPipelineCountTerm[] Per(ShaderPipelineCountBasis basis, ulong elements = 1) => [new(
        Elements: elements,
        Per: [basis]
    )];
    private static ShaderPipelineResource Words(string name, ulong sizeBytes = 16, uint? stride = null, IReadOnlyList<ShaderPipelineCountTerm>? count = null, ShaderPipelineInitialization initialization = ShaderPipelineInitialization.Undefined) => new(
        Count: count,
        Initialization: initialization,
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: name,
        SizeBytes: ((count is null)
            ? sizeBytes
            : null),
        StrideBytes: stride
    );
    private static ShaderPipelinePass Pass(string name, ShaderPipelinePassKind kind, ResourceReference[] inputs, ResourceReference[] outputs, ShaderPipelineDispatch? dispatch = null) => new(
        Dispatch: dispatch,
        EntryPoint: ((kind == ShaderPipelinePassKind.Package)
            ? string.Empty
            : "main"),
        Inputs: inputs,
        Kind: kind,
        Name: name,
        Outputs: outputs,
        Source: ((kind == ShaderPipelinePassKind.Package)
            ? "test.package"
            : $"{name}.hlsl")
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
                    count: Per(
                        basis: ShaderPipelineCountBasis.Instances,
                        elements: 2
                    ),
                    name: "table",
                    stride: 16
                ),
            ]
        ),
        packages: [
            Pass(
                dispatch: ShaderPipelineDispatch.Indirect(arguments: "args"),
                inputs: ["peeked", "table"],
                kind: ShaderPipelinePassKind.Package,
                name: "consume",
                outputs: ["result"]
            ),
            Pass(
                dispatch: ShaderPipelineDispatch.Groups(x: 1),
                inputs: ["args"],
                kind: ShaderPipelinePassKind.Package,
                name: "peek",
                outputs: ["peeked"]
            ),
            Pass(
                inputs: [],
                kind: ShaderPipelinePassKind.Package,
                name: "count",
                outputs: ["args", "table"]
            ),
        ]
    );
    private static void AssertRefused(string code, ShaderPipelineResource[] resources, ShaderPipelinePass[] passes, string[] outputs) {
        var exception = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(
            definition: new ShaderPipelineDefinition(
                name: "refused",
                outputs: outputs,
                passes: passes.Where(predicate: static pass => (pass.Kind != ShaderPipelinePassKind.Package)).ToArray(),
                resources: resources
            ),
            packages: passes.Where(predicate: static pass => (pass.Kind == ShaderPipelinePassKind.Package)).ToArray()
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
            json: """{ "name": "table", "kind": "Buffer", "strideBytes": 16, "count": [{ "per": ["Viewports", "Tiles"], "elements": 4 }, { "per": ["Instances"], "elements": 2 }] }""",
            jsonTypeInfo: ShaderPipelineJsonContext.Default.ShaderPipelineResource
        );
        var pass = System.Text.Json.JsonSerializer.Deserialize(
            json: """{ "name": "consume", "source": "p", "entryPoint": "", "kind": "Compute", "dispatch": { "kind": "Indirect", "arguments": "args", "argumentsOffsetBytes": 12 } }""",
            jsonTypeInfo: ShaderPipelineJsonContext.Default.ShaderPipelinePass
        );
        var expected = Words(
            count: [
                new ShaderPipelineCountTerm(
                    Elements: 4,
                    Per: [ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles]
                ),
                new ShaderPipelineCountTerm(
                    Elements: 2,
                    Per: [ShaderPipelineCountBasis.Instances]
                ),
            ],
            name: "table",
            stride: 16
        );

        Assert.Equal(
            actual: resource! with { Count = null },
            expected: expected with { Count = null }
        );
        Assert.Equal(
            actual: resource.Count,
            expected: expected.Count
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
            Width: 8
        ) {
            DynamicTransforms = 5,
            InstanceGridWords = 11,
            InstanceMaskWords = 2,
            Instances = 7,
            ProgramWords = 100,
            Tiles = 6,
            Viewports = 3,
        };

        Assert.Equal(
            actual: Words(
                count: Per(
                    basis: ShaderPipelineCountBasis.Instances,
                    elements: 2
                ),
                name: "a",
                stride: 16
            ).ResolveSizeBytes(counts: counts),
            expected: ((16UL * 2UL) * 7UL)
        );
        Assert.Equal(
            actual: Words(
                count: Per(basis: ShaderPipelineCountBasis.Extent),
                name: "b",
                stride: 80
            ).ResolveSizeBytes(counts: counts),
            expected: (80UL * 32UL)
        );
        Assert.Equal(
            actual: Words(
                count: Per(basis: ShaderPipelineCountBasis.ProgramWords),
                name: "c"
            ).ResolveSizeBytes(counts: counts),
            expected: 400UL
        );
        // Every basis resolves to its own count.
        Assert.Equal(
            actual: Enum.GetValues<ShaderPipelineCountBasis>().Select(selector: basis => (Words(
                count: Per(basis: basis),
                name: basis.ToString()
            ).ResolveSizeBytes(counts: counts) / 4UL)),
            expected: [32UL, 7UL, 100UL, 3UL, 6UL, 5UL, 2UL, 11UL]
        );
        // A count is the sum of its terms, each the product of its bases' units.
        Assert.Equal(
            actual: Words(
                count: [
                    new ShaderPipelineCountTerm(
                        Elements: 4,
                        Per: [ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles]
                    ),
                    new ShaderPipelineCountTerm(
                        Elements: 12,
                        Per: [ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Instances]
                    ),
                ],
                name: "e"
            ).ResolveSizeBytes(counts: counts),
            expected: (4UL * (((4UL * 3UL) * 6UL) + ((12UL * 3UL) * 7UL)))
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
    [Fact]
    public void AShaderPassIsRefusedEveryShapeAndStorageThePipelineNodeCannotRecord() {
        AssertRefused(
            code: "SHADERPIPE_DISPATCH_PACKAGE",
            outputs: ["out"],
            passes: [Pass(dispatch: ShaderPipelineDispatch.Groups(x: 2), inputs: [], kind: ShaderPipelinePassKind.Compute, name: "grid", outputs: ["out"])],
            resources: [Words(name: "out")]
        );
        AssertRefused(
            code: "SHADERPIPE_DISPATCH_PACKAGE",
            outputs: ["out"],
            passes: [
                Pass(inputs: [], kind: ShaderPipelinePassKind.Compute, name: "write", outputs: ["args"]),
                Pass(dispatch: ShaderPipelineDispatch.Indirect(arguments: "args"), inputs: [], kind: ShaderPipelinePassKind.Compute, name: "read", outputs: ["out"]),
            ],
            resources: [Words(name: "args"), Words(name: "out")]
        );
        AssertRefused(
            code: "SHADERPIPE_PACKAGE_STORAGE",
            outputs: ["out"],
            passes: [Pass(inputs: [], kind: ShaderPipelinePassKind.Compute, name: "write", outputs: ["out"])],
            resources: [Words(name: "out", stride: 16)]
        );
        AssertRefused(
            code: "SHADERPIPE_PACKAGE_STORAGE",
            outputs: ["out"],
            passes: [Pass(inputs: [], kind: ShaderPipelinePassKind.Package, name: "write", outputs: ["out"])],
            resources: [Words(count: Per(basis: ShaderPipelineCountBasis.Instances), name: "out")]
        );
    }
    [Fact]
    public void AMalformedDispatchOrBufferLayoutIsRefusedByName() {
        ShaderPipelinePass Consume(ShaderPipelineDispatch dispatch, ResourceReference[]? inputs = null) => Pass(
            dispatch: dispatch,
            inputs: (inputs ?? []),
            kind: ShaderPipelinePassKind.Package,
            name: "consume",
            outputs: ["out"]
        );
        var writer = Pass(inputs: [], kind: ShaderPipelinePassKind.Package, name: "write", outputs: ["args"]);
        ShaderPipelineResource[] resources = [Words(name: "args"), Words(name: "out")];

        AssertRefused(code: "SHADERPIPE_DISPATCH_ARGUMENTS", outputs: ["out"], passes: [writer, Consume(dispatch: ShaderPipelineDispatch.Indirect(arguments: "args", offsetBytes: 6))], resources: resources);
        AssertRefused(code: "SHADERPIPE_DISPATCH_ARGUMENTS", outputs: ["out"], passes: [writer, Consume(dispatch: ShaderPipelineDispatch.Indirect(arguments: "args", offsetBytes: 8))], resources: resources);
        AssertRefused(code: "SHADERPIPE_DISPATCH_ARGUMENTS", outputs: ["out"], passes: [writer, Consume(dispatch: ShaderPipelineDispatch.Indirect(arguments: "args"), inputs: ["args"])], resources: resources);
        AssertRefused(code: "SHADERPIPE_UNKNOWN_RESOURCE", outputs: ["out"], passes: [writer, Consume(dispatch: ShaderPipelineDispatch.Indirect(arguments: "missing"))], resources: resources);
        AssertRefused(code: "SHADERPIPE_DISPATCH_SHAPE", outputs: ["out"], passes: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 0))], resources: resources);
        AssertRefused(code: "SHADERPIPE_DISPATCH_SHAPE", outputs: ["out"], passes: [writer, Consume(dispatch: new ShaderPipelineDispatch(Arguments: "args", Kind: ShaderPipelineDispatchKind.Extent))], resources: resources);
        AssertRefused(code: "SHADERPIPE_BUFFER_STRIDE", outputs: ["out"], passes: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 1))], resources: [Words(name: "args"), Words(name: "out", sizeBytes: 24, stride: 16)]);
        AssertRefused(code: "SHADERPIPE_BUFFER_STRIDE", outputs: ["out"], passes: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 1))], resources: [Words(name: "args"), Words(name: "out", stride: 6)]);
        AssertRefused(code: "SHADERPIPE_BUFFER_SIZE", outputs: ["out"], passes: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 1))], resources: [Words(name: "args"), Words(name: "out") with { Count = Per(basis: ShaderPipelineCountBasis.Instances) }]);

        // A count has one spelling: at least one term, each with elements, one or more declared bases named once, and
        // no two terms over the same bases.
        IReadOnlyList<ShaderPipelineCountTerm>[] malformed = [
            [],
            Per(basis: ShaderPipelineCountBasis.Instances, elements: 0),
            [new ShaderPipelineCountTerm(Per: [])],
            Per(basis: ((ShaderPipelineCountBasis)99)),
            [new ShaderPipelineCountTerm(Per: [ShaderPipelineCountBasis.Tiles, ShaderPipelineCountBasis.Tiles])],
            [
                new ShaderPipelineCountTerm(Per: [ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles]),
                new ShaderPipelineCountTerm(Elements: 3, Per: [ShaderPipelineCountBasis.Tiles, ShaderPipelineCountBasis.Viewports]),
            ],
        ];

        foreach (var count in malformed) {
            AssertRefused(code: "SHADERPIPE_BUFFER_COUNT", outputs: ["out"], passes: [writer, Consume(dispatch: ShaderPipelineDispatch.Groups(x: 1))], resources: [Words(name: "args"), Words(count: count, name: "out")]);
        }
    }
    [Fact]
    public void ACountedBufferScalingWithABasisTheHostResolvesToZeroIsRefusedNamingTheBasis() {
        var table = Words(
            count: [
                new ShaderPipelineCountTerm(Per: [ShaderPipelineCountBasis.Extent]),
                new ShaderPipelineCountTerm(Per: [ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles]),
            ],
            name: "table"
        );
        var counts = new ShaderPipelineStorageCounts(
            Height: 2,
            Width: 2
        ) {
            Viewports = 1,
        };
        var exception = Assert.Throws<InvalidOperationException>(testCode: () => table.ResolveSizeBytes(counts: counts));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: nameof(ShaderPipelineCountBasis.Tiles)
        );
        Assert.Equal(
            actual: table.ResolveSizeBytes(counts: counts with { Tiles = 5 }),
            expected: (4UL * (4UL + 5UL))
        );
    }
}
