namespace Puck.Hosting;

/// <summary>Exposes held-capability lease lineage inside the hosting assembly so a delegated grant remains linked to
/// every ancestor's revocation state.</summary>
internal interface IHeldCapabilityLeaseSource {
    /// <summary>Resolves the live lease for a held capability type.</summary>
    /// <param name="capabilityType">The held capability's contract type.</param>
    /// <param name="lease">The live lease, when held.</param>
    /// <returns><see langword="true"/> when the context currently holds the capability.</returns>
    bool TryResolveHeldLease(Type capabilityType, out HeldCapabilityLease lease);
}
