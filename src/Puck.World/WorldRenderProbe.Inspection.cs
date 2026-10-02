using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldRenderProbe {
    /// <summary>The primary presenter's existing environment resolver, installed alongside its counter source.</summary>
    public WorldEnvironmentResolve? PrimaryEnvironment { get; set; }

    /// <summary>Returns diagnostics owned by the actual inspected residency. An unknown or retired residency
    /// never inherits the primary world's diagnostics or a replacement registered under the same name.</summary>
    /// <param name="residency">The view's actual followed residency, or null before one is available.</param>
    /// <returns>That owner’s shared guard, or null when the residency has no registered environment.</returns>
    public WorldValueDomainGuard? DomainsOf(SdfWorldResidency? residency) {
        if (residency is null) { return null; }
        if (ReferenceEquals(objA: residency, objB: Residency)) { return PrimaryEnvironment?.Domains; }
        lock (m_gate) {
            foreach (var view in m_views) {
                if (ReferenceEquals(objA: view.Work, objB: residency.Work)) { return view.Environment?.Domains; }
            }
        }
        return null;
    }
}
