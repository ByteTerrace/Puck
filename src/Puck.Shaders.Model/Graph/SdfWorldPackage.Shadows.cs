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
    /// <summary>The incoming visibility image's format: one channel per fade slot, both slots at every nonzero
    /// capacity, since one shadow and one shading kernel serve every capacity.</summary>
    public const GpuPixelFormat IncomingVisibilityFormat = GpuPixelFormat.R8G8Unorm;

    /// <summary>Gets the incoming visibility members every per-view interface declares, whatever the fade capacity. A
    /// graph without the image binds the tables' fillers there, and its passes read a zero fade count.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> IncomingMembers => IncomingDeclarations.Members;

    // A nested holder, so Members can read it whatever order the partial files' static initializers run in.
    private static class IncomingDeclarations {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Members = [
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: IncomingVisibility, type: ShaderValueType.Float2),
            ShaderInterfaceMember.StorageImage(format: IncomingVisibilityFormat, group: ShaderInterfaceGroup.Pass, name: IncomingVisibilityWritten, type: ShaderValueType.Float2),
        ];
    }

    /// <summary>Returns a cached fragment whose incoming image allocation follows policy, independent of active fades:
    /// at a nonzero fade capacity the shadow pass writes, and views reads, a retained image of
    /// <see cref="IncomingVisibilityFormat"/>; at zero the fragment has none, and its passes bind 1x1 fillers.</summary>
    /// <param name="reconstructs">Whether the view reconstructs its render grid.</param>
    /// <param name="temporal">Whether the view reconstructs over time.</param>
    /// <param name="fadeCapacity">The configured number of concurrent fades, from zero to two.</param>
    /// <returns>The fragment. Its incoming image is retained and enters graph memory accounting.</returns>
    public static RenderGraphPackageFragment FragmentFor(bool reconstructs, bool temporal, int fadeCapacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: fadeCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: fadeCapacity, other: 2);
        return ShadowDeclarations.Fragments[(temporal ? 2 : (reconstructs ? 1 : 0)), ((fadeCapacity > 0) ? 1 : 0)];
    }

    private static class ShadowDeclarations {
        internal static readonly RenderGraphPackageFragment[,] Fragments = {
            { NativeFragment, CreateFragment(source: NativeFragment, renderExtent: false) },
            { Fragment, CreateFragment(source: Fragment, renderExtent: true) },
            { TemporalFragment, CreateFragment(source: TemporalFragment, renderExtent: true) },
        };

        private static RenderGraphPackageFragment CreateFragment(RenderGraphPackageFragment source, bool renderExtent) => source with {
            Resources = [.. source.Resources, Image(format: IncomingVisibilityFormat, from: null, name: IncomingVisibility, retained: true) with {
                Dimensions = (renderExtent ? ShaderPipelineDimensions.Render() : ShaderPipelineDimensions.Relative()),
            }],
            Passes = [.. source.Passes.Select(selector: pass => pass.Name switch {
                Parts.Shadow => pass with {
                    Outputs = [.. pass.Outputs, new ResourceReference(Name: IncomingVisibility)],
                    OutputAccesses = [.. pass.OutputAccesses, RenderGraphPortAccess.ComputeWrite],
                },
                Parts.Views => pass with {
                    Inputs = [.. pass.Inputs, new ResourceReference(Name: IncomingVisibility)],
                    InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead],
                },
                _ => pass,
            })],
        };
    }
}
