using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The additional shadow capacities a frame source's authored policies can reach. F = 0 uses the base kernels.</summary>
[Flags]
public enum SdfShadowFadeVariants {
    /// <summary>No additional capacity.</summary>
    None = 0,
    /// <summary>The four kernels for one incoming visibility channel.</summary>
    One = 1,
    /// <summary>The four kernels for two incoming visibility channels.</summary>
    Two = 2,
}
public sealed partial class SdfWorldPipelines {
    private SdfShadowFadeVariants m_shadowFadeVariants;

    /// <summary>Returns the variants required by one policy's incoming capacity.</summary>
    /// <param name="fadeCapacity">The configured F, from zero through two.</param>
    /// <returns>The additional variants, or none for F = 0.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is outside zero through two.</exception>
    public static SdfShadowFadeVariants FadeVariantsFor(int fadeCapacity) => fadeCapacity switch {
        0 => SdfShadowFadeVariants.None,
        1 => SdfShadowFadeVariants.One,
        2 => SdfShadowFadeVariants.Two,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(fadeCapacity)),
    };
    /// <summary>Requests newly reachable fade variants through the shared background pipeline builds. Requests are
    /// idempotent and depend on policy capacity, never on whether a handoff is active. The set keeps these leases until
    /// disposal; <see cref="IsBuilt"/> and residency readiness decide when a frame can use them.</summary>
    /// <param name="cache">The composition's pass-pipeline cache.</param>
    /// <param name="device">The device this set was acquired on.</param>
    /// <param name="variants">The capacities the source can reach, including its current policy.</param>
    public void RequestShadowFadeVariants(GpuPassPipelineCache cache, IGpuDeviceContext device, SdfShadowFadeVariants variants) {
        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
            var missing = variants & ~m_shadowFadeVariants;

            if (missing == SdfShadowFadeVariants.None) { return; }
            foreach (var kernel in BuildOrder(kernels: m_kernels, shadowFadeVariants: missing)) {
                if ((FadeVariantOf(kernel: kernel) == SdfShadowFadeVariants.None) || (m_slots[((int)kernel)] is not null)) { continue; }
                var description = SdfWorldTables.PipelineLayouts.Specs[((int)kernel)].Description;

                m_slots[((int)kernel)] = new Slot(description: description, lease: cache.Acquire(device: device,
                    key: GpuPassPipelineKey.OfCompute(bytecode: m_kernels[kernel], description: description)));
            }
            m_shadowFadeVariants |= missing;
        }
    }

    internal static SdfShadowFadeVariants FadeVariantOf(SdfKernel kernel) => kernel switch {
        SdfKernel.ShadowFade1 or SdfKernel.ViewsFade1 or SdfKernel.ViewsCoreFade1 or SdfKernel.ViewsFoldsFade1 => SdfShadowFadeVariants.One,
        SdfKernel.ShadowFade2 or SdfKernel.ViewsFade2 or SdfKernel.ViewsCoreFade2 or SdfKernel.ViewsFoldsFade2 => SdfShadowFadeVariants.Two,
        _ => SdfShadowFadeVariants.None,
    };
    internal static SdfKernel ShadowKernelOf(int fadeCapacity) => fadeCapacity switch {
        1 => SdfKernel.ShadowFade1,
        2 => SdfKernel.ShadowFade2,
        _ => SdfKernel.Shadow,
    };
}
