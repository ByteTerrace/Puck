using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// Row-region laws for <see cref="ShaderPipelineRenderNode"/>: an array reads the row it is bound to through the World
/// set its pass binds, element <c>i</c> at byte <c>4i</c>; two passes reading one row the same way read one region, bound
/// in both passes' sets, where reading it differently makes two; a rebinding regroups an installed graph's regions and
/// keeps the rows it read; and a staged row region reads exactly the bytes a ring does, frame by frame, under the model
/// that runs the region copy.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    private const int RowLength = 8;
    private const string TilesRow = "state.tiles";

    // A device with a host-visible device-local aperture large enough that every row region rings.
    private static GpuMemoryProfile Aperture => new(
        CoherentUnifiedMemory: false,
        DeviceLocalBytes: (1UL << 30),
        HostVisibleDeviceLocalBytes: (1UL << 28),
        LargestDeviceLocalHeapBytes: (1UL << 30),
        UnifiedMemory: false
    );

    private static Dictionary<string, ShaderArrayField> Tiles(ShaderValueType type = ShaderValueType.Int) => new(comparer: StringComparer.Ordinal) {
        ["tiles"] = new(Length: RowLength, Type: type),
    };
    private static ShaderPipelineRenderNode RowNode(FakePipelineGpu gpu, string accumulateRow, ShaderValueType accumulateType = ShaderValueType.Int) {
        var node = Node(gpu: gpu);

        node.BindRows(rows: [
            new ShaderPipelineRowBinding(Array: "tiles", Pass: "accumulate", Row: accumulateRow),
            new ShaderPipelineRowBinding(Array: "tiles", Pass: "convert", Row: TilesRow),
        ]);
        node.Swap(pipeline: Feedback(
            accumulateArrays: Tiles(type: accumulateType),
            convertArrays: Tiles()
        ));
        _ = node.ProduceUntilInstalled();

        return node;
    }
    // One compute pass reading a tiles array and drawing an image: a graph the region-copy model runs, which creates no
    // graphics pipeline.
    private static CompiledShaderPipeline Board() {
        var plan = new ShaderPipelineCompiler().Compile(definition: new RenderGraphDefinition(
            name: "board",
            outputs: ["image"],
            passes: [
                Pass(
                    inputs: [],
                    kind: ShaderPipelineDocumentPassKind.Compute,
                    name: "draw",
                    outputs: ["image"]
                ) with {
                    Arrays = Tiles(),
                },
            ],
            resources: [
                Image(
                    format: "R8G8B8A8Unorm",
                    name: "image"
                ),
            ]
        ));

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: plan.Passes.ToDictionary(
                elementSelector: static pass => Shader(
                    kind: pass.Declaration!.Kind,
                    name: pass.Name
                ),
                keySelector: static pass => pass.Name
            )
        );
    }
    // The elements a frame's World set at the given place reads, as ints.
    private static int[] RowElements(FakePipelineGpu gpu, int set) {
        var world = gpu.BoundSets.Where(predicate: static bound => (bound.Group == ((uint)ShaderInterfaceGroup.World))).ToArray();
        var bytes = gpu.StorageBytes(
            binding: 0U,
            set: world[set].Set,
            sizeBytes: (RowLength * 4)
        );

        return [.. Enumerable.Range(count: RowLength, start: 0).Select(selector: index => BinaryPrimitives.ReadInt32LittleEndian(source: bytes.AsSpan(start: (index * 4))))];
    }

    /// <summary>An array reads its bound row through the World set: the convert pass binds a set at group 1 on every
    /// frame whose binding holds the row's region, zeros before any write and each written value as an element after
    /// one, elements past the values reading zero; a row no array is bound to is refused.</summary>
    [Fact]
    public void AnArrayReadsItsBoundRowThroughTheWorldSetItsPassBinds() {
        var gpu = new FakePipelineGpu { MemoryProfile = Aperture };
        using var node = Node(gpu: gpu);

        node.BindRows(rows: [new ShaderPipelineRowBinding(Array: "tiles", Pass: "convert", Row: TilesRow)]);
        node.Swap(pipeline: Feedback(convertArrays: Tiles()));
        _ = node.ProduceUntilInstalled();
        Assert.True(condition: node.DeclaresArray(array: "tiles", passName: "convert"));
        Assert.False(condition: node.DeclaresArray(array: "tiles", passName: "accumulate"));

        gpu.Recording = true;
        gpu.BoundSets.Clear();
        Produce(node: node);
        // One pass of three binds the World group, once a frame.
        _ = Assert.Single(
            collection: gpu.BoundSets,
            predicate: static bound => (bound.Group == ((uint)ShaderInterfaceGroup.World))
        );
        Assert.Equal(actual: RowElements(gpu: gpu, set: 0), expected: new int[RowLength]);

        Assert.True(condition: node.TryWriteRow(
            row: TilesRow,
            values: [5d, 0d, 17d, 3d]
        ));
        Assert.False(condition: node.TryWriteRow(row: "state.other", values: [1d]));

        // Every slot's set reads the write on its next frame.
        for (var frame = 0; (frame < ((int)InFlight)); frame++) {
            gpu.BoundSets.Clear();
            Produce(node: node);
            Assert.Equal(actual: RowElements(gpu: gpu, set: 0), expected: [5, 0, 17, 3, 0, 0, 0, 0]);
        }
    }
    /// <summary>Two passes reading one row the same way read one region: the graph holds one row region, both passes'
    /// World sets name its buffer for every slot, and the graph creates one region's buffers fewer than when the passes
    /// read two rows or one row as two element types, each of which holds two regions.</summary>
    [Fact]
    public void TwoPassesReadingOneRowTheSameWayReadOneRegion() {
        (int Regions, int Buffers, nint[] Accumulate, nint[] Convert) Install(string accumulateRow, ShaderValueType accumulateType) {
            var gpu = new FakePipelineGpu { MemoryProfile = Aperture };
            using var node = RowNode(
                accumulateRow: accumulateRow,
                accumulateType: accumulateType,
                gpu: gpu
            );
            var buffers = gpu.CreatedObjects.Count(predicate: static created => (created.Kind == $"host {GpuBufferUsage.Storage} buffer"));

            gpu.Recording = true;
            gpu.BoundSets.Clear();

            var readers = new List<nint>[] { [], [] };

            for (var frame = 0; (frame < ((int)InFlight)); frame++) {
                gpu.BoundSets.Clear();
                Produce(node: node);

                var world = gpu.BoundSets.Where(predicate: static bound => (bound.Group == ((uint)ShaderInterfaceGroup.World))).ToArray();

                Assert.Equal(expected: 2, actual: world.Length);
                readers[0].Add(item: gpu.StorageBuffer(binding: 0U, set: world[0].Set));
                readers[1].Add(item: gpu.StorageBuffer(binding: 0U, set: world[1].Set));
            }

            return (node.RowRegionCount, buffers, [.. readers[0]], [.. readers[1]]);
        }

        var shared = Install(accumulateRow: TilesRow, accumulateType: ShaderValueType.Int);
        var twoRows = Install(accumulateRow: "state.other", accumulateType: ShaderValueType.Int);
        var twoTypes = Install(accumulateRow: TilesRow, accumulateType: ShaderValueType.Float);

        Assert.Equal(actual: shared.Regions, expected: 1);
        Assert.Equal(actual: shared.Accumulate, expected: shared.Convert);
        Assert.Equal(expected: ((int)InFlight), actual: shared.Accumulate.Distinct().Count());
        Assert.Equal(actual: (twoRows.Regions, twoTypes.Regions), expected: (2, 2));
        Assert.Equal(actual: twoRows.Buffers, expected: (shared.Buffers + ((int)InFlight)));
        Assert.Equal(actual: twoTypes.Buffers, expected: twoRows.Buffers);
        Assert.All(
            collection: Enumerable.Range(count: ((int)InFlight), start: 0),
            action: slot => Assert.NotEqual(expected: twoRows.Convert[slot], actual: twoRows.Accumulate[slot])
        );
    }
    /// <summary>A rebinding that moves what an installed graph's arrays read rebuilds it beside the installed one, which
    /// installs with its regions regrouped and every row reading what the host last wrote to it; binding the same rows
    /// again rebuilds nothing.</summary>
    [Fact]
    public void ARebindingRegroupsTheInstalledGraphsRegionsAndKeepsItsRows() {
        var gpu = new FakePipelineGpu { MemoryProfile = Aperture };
        using var node = RowNode(
            accumulateRow: TilesRow,
            gpu: gpu
        );

        Assert.True(condition: node.TryWriteRow(row: TilesRow, values: [4d, 2d]));
        Produce(node: node);
        Assert.Equal(expected: 1, actual: node.RowRegionCount);

        var rows = new[] {
            new ShaderPipelineRowBinding(Array: "tiles", Pass: "accumulate", Row: "state.other"),
            new ShaderPipelineRowBinding(Array: "tiles", Pass: "convert", Row: TilesRow),
        };

        node.BindRows(rows: rows);
        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    Produce(node: node);

                    return (node.RowRegionCount == 2);
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: $"The rebinding never installed: {node.LastSwapError}"
        );
        Assert.True(condition: node.TryWriteRow(row: "state.other", values: [9d]));

        gpu.Recording = true;
        for (var frame = 0; (frame < ((int)InFlight)); frame++) {
            gpu.BoundSets.Clear();
            Produce(node: node);
            Assert.Equal(actual: RowElements(gpu: gpu, set: 0), expected: [9, 0, 0, 0, 0, 0, 0, 0]);
            Assert.Equal(actual: RowElements(gpu: gpu, set: 1), expected: [4, 2, 0, 0, 0, 0, 0, 0]);
        }

        Produce(frames: WarmFrames, node: node);

        var creations = gpu.CreationCount;

        node.BindRows(rows: [.. rows.Reverse()]);
        Produce(frames: WarmFrames, node: node);
        Assert.Equal(expected: creations, actual: gpu.CreationCount);
    }
    /// <summary>A staged row region reads the ring's bytes: the same graph and writes on a device whose memory stages the
    /// region and on one whose aperture rings it leave, after every frame, identical bytes in the buffer the pass's World
    /// set binds, the staged one copied by the region-copy kernel under the model that runs it.</summary>
    [Fact]
    public void AStagedRowRegionReadsTheRingsBytes() {
        static List<byte[]> Readings(GpuMemoryProfile profile) {
            var gpu = new UploadModelGpu(reportVersion: 0) { MemoryProfile = profile };
            using var node = new ShaderPipelineRenderNode(
                deviceContext: gpu,
                height: Extent,
                hostsOnDirectX: false,
                inFlightFrames: InFlight,
                name: "rows",
                packages: new RenderGraphPackageRecorders(regionCopy: new GpuRegionCopyPass(
                    kernel: new byte[] { UploadModelGpu.RegionCopyBytecode },
                    pipelines: new GpuPassPipelineCache()
                )),
                pipelines: new GpuPassPipelineCache(),
                width: Extent
            );

            node.BindRows(rows: [new ShaderPipelineRowBinding(Array: "tiles", Pass: "draw", Row: TilesRow)]);
            node.Swap(pipeline: Board());
            _ = node.ProduceUntilInstalled();

            var readings = new List<byte[]>();

            for (var frame = 0; (frame < 12); frame++) {
                if ((frame % 3) == 1) {
                    Assert.True(condition: node.TryWriteRow(
                        row: TilesRow,
                        values: [frame, (frame * 7), -frame]
                    ));
                }

                _ = node.ProduceFrame(context: default);
                readings.Add(item: gpu.Memory(bufferHandle: gpu.BufferAt(
                    binding: 0U,
                    set: gpu.BoundSet(group: ((uint)ShaderInterfaceGroup.World))
                ))[..(RowLength * 4)]);
            }

            Assert.Empty(collection: gpu.StateConflicts);

            return readings;
        }

        Assert.Equal(
            actual: GpuResidency.Select(byteCount: (RowLength * 4), profile: default, readersInFlight: true),
            expected: GpuResidencyPolicy.Staged
        );

        var staged = Readings(profile: default);

        Assert.Equal(expected: Readings(profile: Aperture), actual: staged);
        Assert.Equal(
            actual: BinaryPrimitives.ReadInt32LittleEndian(source: staged[^1].AsSpan(start: 8)),
            expected: -10
        );
    }
}
