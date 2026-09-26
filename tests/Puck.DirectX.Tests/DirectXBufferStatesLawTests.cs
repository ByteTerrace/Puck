using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Pins the buffer-barrier decisions <see cref="DirectXGpuRecorder.TransitionBuffer"/> records. Direct3D 12
/// decays every buffer to <c>COMMON</c> when the <c>ExecuteCommandLists</c> that used it completes, so a state recorded in
/// one recording must not elide a transition in the next. The SDF engine's views indirect-args buffer is written as a UAV
/// and read by <c>ExecuteIndirect</c> every frame; without its <c>UNORDERED_ACCESS</c> to <c>INDIRECT_ARGUMENT</c>
/// transition, the dispatch reads stale group counts.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXBufferStatesLawTests {
    private const nint ArgsBuffer = 0x1000;
    private const D3D12_RESOURCE_STATES UnorderedAccess = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    private const D3D12_RESOURCE_STATES IndirectArgument = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT;
    private const D3D12_RESOURCE_STATES NonPixelShaderResource = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;

    [Fact]
    public void EveryRecordingTransitionsTheArgsBufferIntoIndirectArgument() {
        var commandBuffer = new DirectXCommandBufferState();
        var transitions = 0;

        for (var recording = 0; (recording < 2); recording++) {
            // What the recorder's BeginCommandBuffer does before any command is recorded.
            commandBuffer.BufferStates.Reset();

            var barrier = commandBuffer.BufferStates.Plan(
                after: IndirectArgument,
                bufferHandle: ArgsBuffer,
                firstState: UnorderedAccess
            );

            if (DirectXBufferBarrierKind.Transition == barrier.Kind) {
                Assert.Equal(
                    expected: UnorderedAccess,
                    actual: barrier.Before
                );
                transitions++;
            }
        }

        Assert.Equal(
            actual: transitions,
            expected: 2
        );
    }
    [Fact]
    public void EachPlacementReachesIndirectArgumentFromTheStateItIsCreatedIn() {
        var indirect = DirectXBufferStates.RequiredState(access: GpuAccess.IndirectCommandRead, stages: GpuStage.ComputeShader);

        Assert.Equal(
            actual: indirect,
            expected: IndirectArgument
        );

        // A host-visible buffer never leaves its upload-heap state, which already covers an indirect-argument read.
        var hostVisible = new DirectXBufferStates().Plan(
            after: indirect,
            bufferHandle: ArgsBuffer,
            firstState: DirectXGpuBufferFactory.HostVisibleState
        );
        // A device-local buffer a shader wrote holds UNORDERED_ACCESS, and moves into INDIRECT_ARGUMENT.
        var written = new DirectXBufferStates().Plan(
            after: indirect,
            bufferHandle: ArgsBuffer,
            firstState: DirectXBufferStates.RequiredState(access: GpuAccess.ShaderWrite, stages: GpuStage.ComputeShader)
        );
        // A device-local buffer nothing wrote in this recording starts from the state it is created in.
        var created = new DirectXBufferStates().Plan(
            after: indirect,
            bufferHandle: ArgsBuffer,
            firstState: DirectXGpuBufferFactory.DeviceLocalState
        );

        Assert.Equal(
            actual: (hostVisible.Kind, hostVisible.Before),
            expected: (DirectXBufferBarrierKind.None, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ)
        );
        Assert.Equal(
            actual: (written.Kind, written.Before, written.After),
            expected: (DirectXBufferBarrierKind.Transition, UnorderedAccess, IndirectArgument)
        );
        Assert.Equal(
            actual: (created.Kind, created.Before, created.After),
            expected: (DirectXBufferBarrierKind.Transition, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, IndirectArgument)
        );
    }
    [Fact]
    public void ARepeatedTransitionInOneRecordingRecordsNothing() {
        var states = new DirectXBufferStates();

        var first = states.Plan(
            after: IndirectArgument,
            bufferHandle: ArgsBuffer,
            firstState: UnorderedAccess
        );
        var second = states.Plan(
            after: IndirectArgument,
            bufferHandle: ArgsBuffer,
            firstState: UnorderedAccess
        );

        Assert.Equal(
            expected: DirectXBufferBarrierKind.Transition,
            actual: first.Kind
        );
        Assert.Equal(
            expected: DirectXBufferBarrierKind.None,
            actual: second.Kind
        );
    }
    [Fact]
    public void AWriteAfterAWriteInOneRecordingOrdersWithAUavBarrier() {
        var states = new DirectXBufferStates();

        var barrier = states.Plan(
            after: UnorderedAccess,
            bufferHandle: ArgsBuffer,
            firstState: UnorderedAccess
        );

        Assert.Equal(
            expected: DirectXBufferBarrierKind.UnorderedAccess,
            actual: barrier.Kind
        );
    }
    [Fact]
    public void AnUploadHeapBufferInGenericReadNeedsNoTransitionForIndirectReads() {
        var states = new DirectXBufferStates();

        var barrier = states.Plan(
            after: IndirectArgument,
            bufferHandle: ArgsBuffer,
            firstState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ
        );

        Assert.Equal(
            expected: DirectXBufferBarrierKind.None,
            actual: barrier.Kind
        );
    }
    [Fact]
    public void EachNeutralAccessNeedsTheStateItsBindingHolds() {
        Assert.Equal(
            actual: DirectXBufferStates.RequiredState(access: GpuAccess.ShaderRead, stages: GpuStage.ComputeShader),
            expected: NonPixelShaderResource
        );
        // A read through a read-write binding is declared with the write the binding permits.
        Assert.Equal(
            actual: DirectXBufferStates.RequiredState(access: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, stages: GpuStage.ComputeShader),
            expected: UnorderedAccess
        );
        Assert.Equal(
            actual: DirectXBufferStates.RequiredState(access: GpuAccess.IndirectCommandRead, stages: GpuStage.ComputeShader),
            expected: IndirectArgument
        );
        Assert.Equal(
            actual: DirectXBufferStates.RequiredState(access: GpuAccess.TransferWrite, stages: GpuStage.ComputeShader),
            expected: UnorderedAccess
        );
        Assert.Equal(
            actual: DirectXBufferStates.RequiredState(access: GpuAccess.None, stages: GpuStage.ComputeShader),
            expected: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
        );
    }
    /// <summary>A read by the fragment stage needs the pixel shader resource state, and the state a graphics reader gets
    /// covers every shader stage: a region the node's copy writes as a UAV and both a fragment and a compute stage read
    /// moves into <c>ALL_SHADER_RESOURCE</c>, which a later compute read already holds, while a compute-only read keeps
    /// <c>NON_PIXEL_SHADER_RESOURCE</c>.</summary>
    [Fact]
    public void AFragmentReadNeedsTheStateEveryShaderStageReadsAndCoversAComputeRead() {
        var states = new DirectXBufferStates();
        var allShaders = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_ALL_SHADER_RESOURCE;

        Assert.Equal(
            actual: (
                DirectXBufferStates.RequiredState(access: GpuAccess.ShaderRead, stages: GpuStage.FragmentShader),
                DirectXBufferStates.RequiredState(access: GpuAccess.ShaderRead, stages: GpuStage.ComputeShader | GpuStage.FragmentShader),
                DirectXBufferStates.RequiredState(access: GpuAccess.ShaderRead, stages: GpuStage.ComputeShader),
                DirectXBufferStates.RequiredState(access: GpuAccess.ShaderWrite, stages: GpuStage.FragmentShader)
            ),
            expected: (allShaders, allShaders, NonPixelShaderResource, UnorderedAccess)
        );

        var copied = states.Plan(
            after: DirectXBufferStates.RequiredState(access: GpuAccess.ShaderRead, stages: GpuStage.ComputeShader | GpuStage.FragmentShader),
            bufferHandle: ArgsBuffer,
            firstState: DirectXBufferStates.RequiredState(access: GpuAccess.ShaderWrite, stages: GpuStage.ComputeShader)
        );
        var computeRead = states.Plan(
            after: DirectXBufferStates.RequiredState(access: GpuAccess.ShaderRead, stages: GpuStage.ComputeShader),
            bufferHandle: ArgsBuffer,
            firstState: UnorderedAccess
        );

        Assert.Equal(
            actual: (copied.Kind, copied.Before, copied.After, computeRead.Kind),
            expected: (DirectXBufferBarrierKind.Transition, UnorderedAccess, allShaders, DirectXBufferBarrierKind.None)
        );
    }
    [Fact]
    public void ACullBufferWrittenReadAndReboundTransitionsAtEveryBindingChange() {
        // The SDF engine's cull buffer in one frame: the beam writes it, cull-args reads it through a read-only binding,
        // the hit passes read it through the views layout's read-write binding, and the composite reads it read-only.
        var states = new DirectXBufferStates();
        (GpuAccess Before, GpuAccess After)[] edges = [
            (GpuAccess.ShaderWrite, GpuAccess.ShaderRead),
            (GpuAccess.ShaderRead, GpuAccess.ShaderRead | GpuAccess.ShaderWrite),
            (GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuAccess.ShaderRead),
        ];
        (D3D12_RESOURCE_STATES Before, D3D12_RESOURCE_STATES After)[] expected = [
            (UnorderedAccess, NonPixelShaderResource),
            (NonPixelShaderResource, UnorderedAccess),
            (UnorderedAccess, NonPixelShaderResource),
        ];

        for (var index = 0; (index < edges.Length); index++) {
            var barrier = states.Plan(
                after: DirectXBufferStates.RequiredState(access: edges[index].After, stages: GpuStage.ComputeShader),
                bufferHandle: ArgsBuffer,
                firstState: DirectXBufferStates.RequiredState(access: edges[index].Before, stages: GpuStage.ComputeShader)
            );

            Assert.Equal(
                actual: (barrier.Kind, barrier.Before, barrier.After),
                expected: (DirectXBufferBarrierKind.Transition, expected[index].Before, expected[index].After)
            );
        }
    }
}
