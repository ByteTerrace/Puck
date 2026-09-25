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
            actual: DirectXBufferStates.RequiredState(access: GpuComputeAccess.ShaderRead),
            expected: NonPixelShaderResource
        );
        // A read through a read-write binding is declared with the write the binding permits.
        Assert.Equal(
            actual: DirectXBufferStates.RequiredState(access: GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite),
            expected: UnorderedAccess
        );
        Assert.Equal(
            actual: DirectXBufferStates.RequiredState(access: GpuComputeAccess.IndirectCommandRead),
            expected: IndirectArgument
        );
        Assert.Equal(
            actual: DirectXBufferStates.RequiredState(access: GpuComputeAccess.TransferWrite),
            expected: UnorderedAccess
        );
        Assert.Equal(
            actual: DirectXBufferStates.RequiredState(access: GpuComputeAccess.None),
            expected: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
        );
    }
    [Fact]
    public void ACullBufferWrittenReadAndReboundTransitionsAtEveryBindingChange() {
        // The SDF engine's cull buffer in one frame: the beam writes it, cull-args reads it through a read-only binding,
        // the hit passes read it through the views layout's read-write binding, and the composite reads it read-only.
        var states = new DirectXBufferStates();
        (GpuComputeAccess Before, GpuComputeAccess After)[] edges = [
            (GpuComputeAccess.ShaderWrite, GpuComputeAccess.ShaderRead),
            (GpuComputeAccess.ShaderRead, GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite),
            (GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite, GpuComputeAccess.ShaderRead),
        ];
        (D3D12_RESOURCE_STATES Before, D3D12_RESOURCE_STATES After)[] expected = [
            (UnorderedAccess, NonPixelShaderResource),
            (NonPixelShaderResource, UnorderedAccess),
            (UnorderedAccess, NonPixelShaderResource),
        ];

        for (var index = 0; (index < edges.Length); index++) {
            var barrier = states.Plan(
                after: DirectXBufferStates.RequiredState(access: edges[index].After),
                bufferHandle: ArgsBuffer,
                firstState: DirectXBufferStates.RequiredState(access: edges[index].Before)
            );

            Assert.Equal(
                actual: (barrier.Kind, barrier.Before, barrier.After),
                expected: (DirectXBufferBarrierKind.Transition, expected[index].Before, expected[index].After)
            );
        }
    }
}
