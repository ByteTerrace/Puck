using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Holds, without a device, every barrier side <see cref="DirectXBarriers"/> can build (texture transitions
/// between every pair of layouts, simultaneous-access and memory dependencies, buffer dependencies over every stage and
/// access mask, the swap chain's hand-overs and the acceleration-structure constants) to Microsoft's Enhanced Barriers
/// specification (<c>d3d/D3D12EnhancedBarriers.md</c> in microsoft/DirectX-Specs): "Layout Access Compatibility",
/// "Access Bits Barrier Sync Compatibility" and the synchronization section's rule that <c>SYNC_NONE</c> pairs only
/// with <c>NO_ACCESS</c>. The tables here are transcribed from the specification independently of the backend's, and
/// carry the two places the runtime is stricter than its text, each named where it applies. A side the runtime refuses
/// fails the whole command list's <c>Close</c> with E_INVALIDARG, which no headless boot reaches.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXBarrierCompatibilityLawTests {
    [Fact]
    public void EveryTextureTransitionStatesOnlyWhatItsLayoutsAndSyncsPermit() {
        var failures = new List<string>();

        foreach (var oldLayout in Enum.GetValues<GpuImageLayout>()) {
            foreach (var newLayout in Enum.GetValues<GpuImageLayout>()) {
                if (!DirectXBarriers.ChangesLayout(newLayout: newLayout, oldLayout: oldLayout)) {
                    continue;
                }

                foreach (var stages in AllStages()) {
                    var barrier = DirectXBarriers.Texture(
                        destinationStages: stages,
                        newLayout: newLayout,
                        oldLayout: oldLayout,
                        resource: null,
                        sourceStages: stages
                    );
                    var context = $"texture {oldLayout}->{newLayout} stages={stages}";

                    CheckTextureSide(barrier.LayoutBefore, barrier.SyncBefore, barrier.AccessBefore, $"{context} before", failures);
                    CheckTextureSide(barrier.LayoutAfter, barrier.SyncAfter, barrier.AccessAfter, $"{context} after", failures);

                    if (barrier.LayoutAfter == D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_UNDEFINED) {
                        failures.Add(item: $"{context}: enters the undefined layout");
                    }
                }
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: Report(failures: failures));
    }
    [Fact]
    public void EverySimultaneousAccessTransitionStatesOnlyCompatibleSyncs() {
        var failures = new List<string>();

        foreach (var oldLayout in Enum.GetValues<GpuImageLayout>()) {
            foreach (var newLayout in Enum.GetValues<GpuImageLayout>()) {
                foreach (var stages in AllStages()) {
                    var barrier = DirectXBarriers.Fixed(
                        destinationStages: stages,
                        newLayout: newLayout,
                        oldLayout: oldLayout,
                        sourceStages: stages
                    );

                    CheckGlobal(barrier: barrier, context: $"simultaneous {oldLayout}->{newLayout} stages={stages}", failures: failures);
                }
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: Report(failures: failures));
    }
    [Fact]
    public void EveryMemoryAndBufferDependencyStatesOnlyCompatibleSyncs() {
        var failures = new List<string>();

        foreach (var stages in AllStages()) {
            for (var bits = 0U; (bits <= AllAccess); ++bits) {
                var access = ((GpuAccess)bits);

                CheckGlobal(
                    barrier: DirectXBarriers.Global(destinationAccess: access, destinationStages: stages, sourceAccess: access, sourceStages: stages),
                    context: $"memory access={access} stages={stages}",
                    failures: failures
                );

                foreach (var unordered in (ReadOnlySpan<bool>)[false, true]) {
                    var barrier = DirectXBarriers.Buffer(
                        destinationAccess: access,
                        destinationStages: stages,
                        resource: null,
                        sourceAccess: access,
                        sourceStages: stages,
                        unorderedAccess: unordered
                    );
                    var context = $"buffer access={access} stages={stages} unordered={unordered}";

                    CheckSide(barrier.SyncBefore, barrier.AccessBefore, $"{context} before", failures);
                    CheckSide(barrier.SyncAfter, barrier.AccessAfter, $"{context} after", failures);

                    if (barrier.SyncBefore == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE) {
                        failures.Add(item: $"{context}: promises no earlier access in the execution");
                    }
                }

                if (failures.Count > 64) {
                    break;
                }
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: Report(failures: failures));
    }
    [Fact]
    public void ThePresentHandOversAndTheAccelerationStructureOrderingsAreCompatible() {
        var failures = new List<string>();

        foreach (var toPresent in (ReadOnlySpan<bool>)[false, true]) {
            var barrier = DirectXBarriers.Present(backBuffer: null, toPresent: toPresent);

            CheckTextureSide(barrier.LayoutBefore, barrier.SyncBefore, barrier.AccessBefore, $"present toPresent={toPresent} before", failures);
            CheckTextureSide(barrier.LayoutAfter, barrier.SyncAfter, barrier.AccessAfter, $"present toPresent={toPresent} after", failures);
        }

        CheckGlobal(barrier: DirectXBarriers.AccelerationStructureInputs, context: "structure inputs", failures: failures);
        CheckGlobal(barrier: DirectXBarriers.AccelerationStructureWritten, context: "structure written", failures: failures);
        CheckGlobal(barrier: DirectXBarriers.PostbuildInfoWrite, context: "postbuild write", failures: failures);
        CheckGlobal(barrier: DirectXBarriers.PostbuildInfoRead, context: "postbuild read", failures: failures);

        Assert.True(condition: (failures.Count == 0), userMessage: Report(failures: failures));
    }
    [Fact]
    public void TheDepthAttachmentLayoutStatesTheWriteAccessAlone() {
        // The specification's layout table lists DEPTH_STENCIL_READ for the depth-write layout; the runtime refuses it
        // ("AccessAfter bits D3D12_BARRIER_ACCESS_DEPTH_STENCIL_READ are incompatible with LayoutAfter
        // D3D12_BARRIER_LAYOUT_DEPTH_STENCIL_WRITE"), and the specification's legacy mapping gives DEPTH_WRITE the write
        // access alone.
        Assert.Equal(
            expected: D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_DEPTH_STENCIL_WRITE,
            actual: DirectXBarriers.Layout(layout: GpuImageLayout.DepthAttachment)
        );
        Assert.Equal(
            expected: D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_WRITE,
            actual: DirectXBarriers.LayoutAccess(layout: GpuImageLayout.DepthAttachment)
        );
    }

    private const uint AllAccess = 0x7FFU;
    private const uint AllStageBits = 0x1FFU;

    // "Layout Access Compatibility": the accesses a texture side may state in each layout the backend uses. The
    // depth-write row lists DEPTH_STENCIL_READ in the specification; the runtime refuses it, so it is left out here.
    private static D3D12_BARRIER_ACCESS LayoutPermits(D3D12_BARRIER_LAYOUT layout) => layout switch {
        D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_UNDEFINED => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS,
        D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COMMON => (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE),
        D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_RENDER_TARGET => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET,
        D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_UNORDERED_ACCESS => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS,
        D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_DEPTH_STENCIL_WRITE => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_WRITE,
        D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_DEPTH_STENCIL_READ => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_READ,
        D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_SHADER_RESOURCE => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE,
        D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COPY_SOURCE => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE,
        D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COPY_DEST => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(layout), actualValue: layout, message: "The law's layout table has no row for this layout."),
    };
    // "Access Bits Barrier Sync Compatibility": at least one of these syncs must accompany the access bit. d3d12.h gives
    // PREDICATION the INDIRECT_ARGUMENT bit, so that bit carries both rows.
    private static D3D12_BARRIER_SYNC Serving(D3D12_BARRIER_ACCESS bit) => bit switch {
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RENDER_TARGET),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VERTEX_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_PIXEL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_CLEAR_UNORDERED_ACCESS_VIEW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EMIT_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RAYTRACING),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_WRITE or D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_READ => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DEPTH_STENCIL),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VERTEX_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_PIXEL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RAYTRACING),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_INDIRECT_ARGUMENT => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EXECUTE_INDIRECT | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_PREDICATION),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST or D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_READ => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RAYTRACING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EMIT_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RAYTRACING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY_RAYTRACING_ACCELERATION_STRUCTURE),
        _ => D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE,
    };

    private static IEnumerable<GpuStage> AllStages() {
        for (var bits = 0U; (bits <= AllStageBits); ++bits) {
            yield return ((GpuStage)bits);
        }
    }
    private static void CheckTextureSide(D3D12_BARRIER_LAYOUT layout, D3D12_BARRIER_SYNC sync, D3D12_BARRIER_ACCESS access, string context, List<string> failures) {
        var permitted = LayoutPermits(layout: layout);

        if ((access != D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS) && ((access & ~permitted) != 0)) {
            failures.Add(item: $"{context}: access {access} outside what layout {layout} permits ({permitted})");
        }

        CheckSide(sync: sync, access: access, context: context, failures: failures);
    }
    private static void CheckGlobal(D3D12_GLOBAL_BARRIER barrier, string context, List<string> failures) {
        CheckSide(sync: barrier.SyncBefore, access: barrier.AccessBefore, context: $"{context} before", failures: failures);
        CheckSide(sync: barrier.SyncAfter, access: barrier.AccessAfter, context: $"{context} after", failures: failures);
    }
    // One side: SYNC_NONE pairs only with NO_ACCESS (the specification's synchronization section); every access bit has a
    // serving sync bit (its sync table); and every sync bit serves some access bit, which the runtime checks beyond the
    // table's text ("SyncAfter bits D3D12_BARRIER_SYNC_COPY are incompatible with AccessAfter bits
    // D3D12_BARRIER_ACCESS_UNORDERED_ACCESS"). The common access and no access accept any sync.
    private static void CheckSide(D3D12_BARRIER_SYNC sync, D3D12_BARRIER_ACCESS access, string context, List<string> failures) {
        if ((sync == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE) && (access != D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS)) {
            failures.Add(item: $"{context}: sync none beside access {access}");
            return;
        }
        if ((access == D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS) || (access == D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON)) {
            return;
        }

        var permitted = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE;

        for (var bits = ((uint)access); (bits != 0U); bits &= (bits - 1U)) {
            var bit = ((D3D12_BARRIER_ACCESS)(bits & (0U - bits)));
            var serving = Serving(bit: bit);

            if (serving == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE) {
                failures.Add(item: $"{context}: access bit {bit} has no row in the law's sync table");
            } else if ((sync & serving) == 0) {
                failures.Add(item: $"{context}: access bit {bit} has no serving sync in {sync}");
            }

            permitted |= serving;
        }

        if ((sync & ~permitted) != 0) {
            failures.Add(item: $"{context}: sync bits {(sync & ~permitted)} serve no access in {access}");
        }
    }
    private static string Report(List<string> failures) =>
        $"{failures.Count} incompatible barrier sides:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: failures.Take(count: 40))}";
}
