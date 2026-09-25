using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Pins the buffer transitions the SDF engine records per command list. Every buffer one dispatch writes and a
/// later dispatch reads goes through one declared transition with the exact accesses on both sides, so Direct3D 12 moves
/// the buffer between <c>UNORDERED_ACCESS</c> and the read states its bindings need and Vulkan records a buffer memory
/// barrier with the matching scopes.</summary>
public sealed class SdfFrameBufferPlanLawTests {
    private static readonly SdfFramePass[] Uploads = [
        SdfFramePass.UploadViewports,
        SdfFramePass.UploadDynamicTransforms,
        SdfFramePass.UploadInstanceGrid,
    ];
    private static readonly SdfFramePass[] RenderedFrame = [
        .. Uploads,
        SdfFramePass.Sky,
        SdfFramePass.Mask,
        SdfFramePass.Beam,
        SdfFramePass.CullArgs,
        SdfFramePass.Primary,
        SdfFramePass.Surface,
        SdfFramePass.Ambient,
        SdfFramePass.Views,
        SdfFramePass.Composite,
    ];

    private static SdfBufferEdge Edge(SdfFrameBuffer buffer, SdfFramePass producer, SdfFramePass consumer, SdfBufferAccess before, SdfBufferAccess after) =>
        new(
            After: after,
            Before: before,
            Buffer: buffer,
            Consumer: consumer,
            Producer: producer
        );

    [Fact]
    public void ARenderedFrameRecordsTheInventoriedEdges() {
        SdfBufferEdge[] expected = [
            Edge(after: SdfBufferAccess.Read, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.Viewports, consumer: SdfFramePass.Sky, producer: SdfFramePass.UploadViewports),
            Edge(after: SdfBufferAccess.Read, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.DynamicTransforms, consumer: SdfFramePass.Sky, producer: SdfFramePass.UploadDynamicTransforms),
            Edge(after: SdfBufferAccess.Read, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.InstanceGrid, consumer: SdfFramePass.Mask, producer: SdfFramePass.UploadInstanceGrid),
            Edge(after: SdfBufferAccess.Read, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.InstanceMasks, consumer: SdfFramePass.Beam, producer: SdfFramePass.Mask),
            Edge(after: SdfBufferAccess.Read, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.Tiles, consumer: SdfFramePass.CullArgs, producer: SdfFramePass.Beam),
            Edge(after: SdfBufferAccess.IndirectRead, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.ViewsArgs, consumer: SdfFramePass.Primary, producer: SdfFramePass.CullArgs),
            Edge(after: SdfBufferAccess.Read, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.CullBounds, consumer: SdfFramePass.Primary, producer: SdfFramePass.CullArgs),
            Edge(after: SdfBufferAccess.ReadWrite, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.PrimaryHits, consumer: SdfFramePass.Surface, producer: SdfFramePass.Primary),
            Edge(after: SdfBufferAccess.ReadWrite, before: SdfBufferAccess.ReadWrite, buffer: SdfFrameBuffer.PrimaryHits, consumer: SdfFramePass.Ambient, producer: SdfFramePass.Surface),
            Edge(after: SdfBufferAccess.Read, before: SdfBufferAccess.ReadWrite, buffer: SdfFrameBuffer.PrimaryHits, consumer: SdfFramePass.Views, producer: SdfFramePass.Ambient),
        ];

        Assert.Equal(
            actual: SdfFrameBufferPlan.Edges(passes: RenderedFrame),
            expected: expected
        );
    }
    [Fact]
    public void BrickWorkOrdersThePoolBeforeTheBeamReadsIt() {
        var edges = SdfFrameBufferPlan.Edges(passes: [SdfFramePass.BrickUpload, SdfFramePass.BrickBake, .. RenderedFrame]);

        Assert.Contains(
            collection: edges,
            expected: Edge(after: SdfBufferAccess.Write, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.BrickPool, consumer: SdfFramePass.BrickBake, producer: SdfFramePass.BrickUpload)
        );
        Assert.Contains(
            collection: edges,
            expected: Edge(after: SdfBufferAccess.Read, before: SdfBufferAccess.Write, buffer: SdfFrameBuffer.BrickPool, consumer: SdfFramePass.Beam, producer: SdfFramePass.BrickBake)
        );
        Assert.Equal(
            actual: edges.Length,
            expected: 12
        );
    }
    [Fact]
    public void ASkippedFrameOwesNoTransition() {
        Assert.Empty(collection: SdfFrameBufferPlan.Edges(passes: [.. Uploads, SdfFramePass.Composite]));
    }
    [Fact]
    public void EveryHazardInARecordingHasExactlyOneEdge() {
        SdfFramePass[] recording = [SdfFramePass.BrickUpload, SdfFramePass.BrickBake, .. RenderedFrame];
        var edges = SdfFrameBufferPlan.Edges(passes: recording);
        var expected = 0;

        foreach (var buffer in Enum.GetValues<SdfFrameBuffer>()) {
            SdfBufferAccess? last = null;

            foreach (var pass in recording) {
                foreach (var use in SdfFrameBufferPlan.Uses(pass: pass)) {
                    if (use.Buffer != buffer) {
                        continue;
                    }

                    if (
                        (last is { } before) &&
                        (SdfFrameBufferPlan.Writes(access: before) || SdfFrameBufferPlan.Writes(access: use.Access) || (before != use.Access))
                    ) {
                        expected++;
                        Assert.Single(collection: edges, predicate: edge => ((edge.Buffer == buffer) && (edge.Consumer == pass)));
                    }

                    last = use.Access;
                }
            }
        }

        Assert.Equal(
            actual: edges.Length,
            expected: expected
        );
    }
    [Fact]
    public void RepeatedIdenticalReadsOweNothing() {
        Assert.False(condition: SdfFrameBufferPlan.NeedsTransition(
            after: SdfBufferAccess.Read,
            before: SdfBufferAccess.Read
        ));
        Assert.False(condition: SdfFrameBufferPlan.NeedsTransition(
            after: SdfBufferAccess.IndirectRead,
            before: SdfBufferAccess.IndirectRead
        ));
    }
    [Fact]
    public void EachAccessDeclaresTheScopeItsBindingUses() {
        Assert.Equal(
            actual: SdfFrameBufferPlan.Declared(access: SdfBufferAccess.Read),
            expected: GpuAccess.ShaderRead
        );
        Assert.Equal(
            actual: SdfFrameBufferPlan.Declared(access: SdfBufferAccess.Write),
            expected: GpuAccess.ShaderWrite
        );
        Assert.Equal(
            actual: SdfFrameBufferPlan.Declared(access: SdfBufferAccess.ReadWrite),
            expected: GpuAccess.ShaderRead | GpuAccess.ShaderWrite
        );
        Assert.Equal(
            actual: SdfFrameBufferPlan.Declared(access: SdfBufferAccess.IndirectRead),
            expected: GpuAccess.IndirectCommandRead
        );
    }
    [Fact]
    public void AHitPassReadsEveryBufferItDoesNotWriteThroughAReadOnlyBinding() {
        // Primary, surface and ambient write the hit records; nothing else a hit pass binds is written, so every other
        // use is a plain read (or the indirect arguments) and consecutive hit passes owe no transition for it.
        foreach (var pass in ((ReadOnlySpan<SdfFramePass>)[SdfFramePass.Primary, SdfFramePass.Surface, SdfFramePass.Ambient, SdfFramePass.Views])) {
            foreach (var use in SdfFrameBufferPlan.Uses(pass: pass)) {
                if (use.Buffer == SdfFrameBuffer.PrimaryHits) {
                    continue;
                }

                Assert.Contains(
                    collection: ((SdfBufferAccess[])[SdfBufferAccess.Read, SdfBufferAccess.IndirectRead]),
                    expected: use.Access
                );
            }
        }

        Assert.Equal(
            actual: Assert.Single(
                collection: SdfFrameBufferPlan.Uses(pass: SdfFramePass.Views).ToArray(),
                predicate: static use => (use.Buffer == SdfFrameBuffer.PrimaryHits)
            ).Access,
            expected: SdfBufferAccess.Read
        );
    }
    [Fact]
    public void TheIndirectArgumentsAreReadAtTheIndirectStage() {
        var edge = Assert.Single(
            collection: SdfFrameBufferPlan.Edges(passes: RenderedFrame),
            predicate: static edge => (edge.Buffer == SdfFrameBuffer.ViewsArgs)
        );

        Assert.Equal(
            actual: (edge.SourceAccess, edge.SourceStage, edge.DestinationAccess, edge.DestinationStage),
            expected: (GpuAccess.ShaderWrite, GpuStage.ComputeShader, GpuAccess.IndirectCommandRead, GpuStage.DrawIndirect)
        );
    }
    [Fact]
    public void NoPassTouchesMoreBuffersThanTheEdgeScratchHolds() {
        foreach (var pass in Enum.GetValues<SdfFramePass>()) {
            Assert.InRange(
                actual: SdfFrameBufferPlan.Uses(pass: pass).Length,
                high: SdfFrameBufferPlan.MaxUsesPerPass,
                low: 0
            );
        }
    }
}
