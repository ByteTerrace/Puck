using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    /// <inheritdoc/>
    public bool OwnsBuffer(string? part) => part == SdfWorldPackage.LightDepth;

    /// <inheritdoc/>
    public IGpuBuffer? BorrowedBuffer(RenderGraphPackageRecorderContext context, IDisposable? built, ShaderPipelineResource resource) =>
        context.Part == SdfWorldPackage.LightDepth ? ((Built)built!).LightBank!.Buffer : null;

    private readonly Dictionary<string, SdfWorldResidency> m_lightViews = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<SdfWorldResidency, string> m_lightNames = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, RenderGraphPackageFragment> m_lightFragments = [];

    /// <summary>Registers the residency's sole conservative depth-camera instance.</summary>
    /// <param name="name">The generated instance name.</param>
    /// <param name="residency">The existing residency whose tables it reads.</param>
    public void RegisterLightView(string name, SdfWorldResidency residency) {
        m_lightViews[name] = residency;
        m_lightNames[residency] = name;
        if (m_entries.TryGetValue(key: name, value: out var entry)) { entry.Frame = -1; }
    }
    /// <summary>Forgets a light camera whose cache is no longer demanded.</summary>
    /// <param name="name">The generated instance name.</param>
    public void UnregisterLightView(string name) {
        if (m_lightViews.Remove(key: name, value: out var residency)) { m_lightNames.Remove(key: residency); }
    }
    internal string? LightViewName(SdfWorldResidency residency) => m_lightNames.GetValueOrDefault(key: residency);
    private RenderGraphPackageFragment LightFragment(SdfWorldResidency residency) {
        var maps = LightMapCount(residency: residency);
        if (!m_lightFragments.TryGetValue(key: maps, value: out var fragment)) {
            fragment = SdfWorldPackage.LightViewFragment(maps: maps);
            m_lightFragments.Add(key: maps, value: fragment);
        }
        return fragment;
    }
    internal static int LightMapCount(SdfWorldResidency? residency) {
        if ((residency is null) || (residency.IndirectTier == Puck.SignedDistance.SdfIndirectTier.Off)) { return 0; }
        var cache = residency.Tables?.Indirect;
        var slots = (cache is { HasLightingCycle: true } ? cache.Lighting!.Frame.Lights.ShadowSlots : residency.Frame?.Lights.ShadowSlots);
        return (((slots?.SlotCount ?? 0) + (slots?.FadeCapacity ?? 0)) * SdfIndirectLightLayout.RegionsPerLight);
    }
}
