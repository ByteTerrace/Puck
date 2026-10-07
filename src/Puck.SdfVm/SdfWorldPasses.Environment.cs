using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    private readonly Dictionary<SdfWorldResidency, (string Name, int View)> m_environmentNames = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RenderGraphPackageFragment, RenderGraphPackageFragment> m_environmentFragments = new(comparer: ReferenceEqualityComparer.Instance);

    internal void RegisterEnvironment(string name, SdfWorldResidency residency, int view) => m_environmentNames[residency] = (name, view);
    internal void UnregisterEnvironment(SdfWorldResidency residency) => m_environmentNames.Remove(key: residency);

    /// <summary>Returns the residency's shared environment producer name, or null before it is registered.</summary>
    /// <param name="residency">The existing residency.</param>
    /// <returns>The producer exporting the map and coefficients as separate buffer dependencies.</returns>
    public string? EnvironmentName(SdfWorldResidency residency) => (m_environmentNames.TryGetValue(key: residency, value: out var source) ? source.Name : null);

    private RenderGraphPackageFragment WithEnvironment(RenderGraphPackageFragment fragment, SdfWorldResidency? residency) {
        if ((residency is null) || (EnvironmentName(residency: residency) is null)) { return fragment; }
        if (!m_environmentFragments.TryGetValue(key: fragment, value: out var environment)) {
            environment = SdfSkyEnvironmentGraph.WithEnvironment(fragment: fragment);
            m_environmentFragments.Add(key: fragment, value: environment);
        }
        return environment;
    }
}
