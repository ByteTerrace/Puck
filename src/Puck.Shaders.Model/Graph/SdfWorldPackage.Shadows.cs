using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The render-grid K history and receiver validation words read by the shadow stage.</summary>
    public const string ShadowHistory = "shadowHistory";
    /// <summary>The current render-grid K history written by the shadow stage.</summary>
    public const string ShadowHistoryWritten = "shadowHistoryRW";
    /// <summary>The secondary shadow amortization switch in the pass block.</summary>
    public const string ShadowAmortize = "shadowAmortize";
    /// <summary>The stable slots whose names or handoffs invalidate history.</summary>
    public const string ShadowOwnershipReject = "shadowOwnershipReject";
    /// <summary>The stable slots whose penumbra anchors invalidate history.</summary>
    public const string ShadowLightReject = "shadowLightReject";
    /// <summary>The words per history pixel: packed K, receiver identity, full ray distance, sample index and
    /// rejection reactivity. The views pass transfers reactivity into temporal color reconstruction.</summary>
    public const uint ShadowHistoryWords = 5;
    /// <summary>The incoming handoff visibilities, sampled by shading.</summary>
    public const string IncomingVisibility = "incomingVisibility";
    /// <summary>The incoming handoff visibilities, written by the shadow stage.</summary>
    public const string IncomingVisibilityWritten = "incomingVisibilityRW";

    /// <summary>Returns the world members for the configured fade capacity. Zero has no incoming image binding.</summary>
    /// <param name="fadeCapacity">The configured number of concurrent fades, from zero to two.</param>
    /// <returns>The pass members, with unchanged common binding and block offsets.</returns>
    public static IReadOnlyList<ShaderInterfaceMember> MembersForShadows(int fadeCapacity) => fadeCapacity switch {
        0 => Members,
        1 => ShadowDeclarations.One,
        2 => ShadowDeclarations.Two,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(fadeCapacity)),
    };
    /// <summary>Returns a cached fragment whose incoming image allocation follows policy, independent of active fades.</summary>
    /// <param name="reconstructs">Whether the view reconstructs its render grid.</param>
    /// <param name="temporal">Whether the view reconstructs over time.</param>
    /// <param name="fadeCapacity">The configured number of concurrent fades, from zero to two.</param>
    /// <returns>The fragment. Its incoming image is transient and enters graph memory accounting.</returns>
    public static RenderGraphPackageFragment FragmentFor(bool reconstructs, bool temporal, int fadeCapacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: fadeCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: fadeCapacity, other: 2);
        return ShadowDeclarations.Fragments[(temporal ? 2 : (reconstructs ? 1 : 0)), fadeCapacity];
    }

    private static class ShadowDeclarations {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> One = CreateMembers(capacity: 1);
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Two = CreateMembers(capacity: 2);
        internal static readonly RenderGraphPackageFragment[,] Fragments = {
            { NativeFragment, CreateFragment(source: NativeFragment, capacity: 1, renderExtent: false), CreateFragment(source: NativeFragment, capacity: 2, renderExtent: false) },
            { Fragment, CreateFragment(source: Fragment, capacity: 1, renderExtent: true), CreateFragment(source: Fragment, capacity: 2, renderExtent: true) },
            { TemporalFragment, CreateFragment(source: TemporalFragment, capacity: 1, renderExtent: true), CreateFragment(source: TemporalFragment, capacity: 2, renderExtent: true) },
        };

        private static IReadOnlyList<ShaderInterfaceMember> CreateMembers(int capacity) => [
            .. Members,
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: IncomingVisibility, type: ((capacity == 1) ? ShaderValueType.Float : ShaderValueType.Float2)),
            ShaderInterfaceMember.StorageImage(format: Format(capacity: capacity), group: ShaderInterfaceGroup.Pass, name: IncomingVisibilityWritten, type: ((capacity == 1) ? ShaderValueType.Float : ShaderValueType.Float2)),
        ];
        private static GpuPixelFormat Format(int capacity) => ((capacity == 1) ? GpuPixelFormat.R8Unorm : GpuPixelFormat.R8G8Unorm);
        private static RenderGraphPackageFragment CreateFragment(RenderGraphPackageFragment source, int capacity, bool renderExtent) => source with {
            Resources = [.. source.Resources, Image(format: Format(capacity: capacity), from: null, name: IncomingVisibility, transient: true) with {
                Dimensions = (renderExtent ? ShaderPipelineDimensions.Render() : ShaderPipelineDimensions.Relative()),
            }],
            Passes = [.. source.Passes.Select(selector: pass => pass.Name switch {
                Parts.Shadow => pass with {
                    Members = MembersForShadows(fadeCapacity: capacity),
                    Outputs = [.. pass.Outputs, new ResourceReference(Name: IncomingVisibility)],
                    OutputAccesses = [.. pass.OutputAccesses, RenderGraphPortAccess.ComputeWrite],
                },
                Parts.Views => pass with {
                    Members = MembersForShadows(fadeCapacity: capacity),
                    Inputs = [.. pass.Inputs, new ResourceReference(Name: IncomingVisibility)],
                    InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead],
                },
                _ => pass,
            })],
        };
    }
}
