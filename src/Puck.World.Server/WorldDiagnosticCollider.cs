using Puck.Physics;

namespace Puck.World.Server;

/// <summary>An effective solver collider and the authored source that produced it.</summary>
/// <param name="Collider">The exact compiled collision primitive.</param>
/// <param name="Stamp">Placement or screen id.</param>
/// <param name="Creation">Creation id.</param>
/// <param name="Node">Shape name or id.</param>
/// <param name="Operation">Authored primitive and blend.</param>
/// <param name="Margin">Authored collision margin, in metres.</param>
/// <param name="Opaque">Whether this authored shape participates in the opaque drawn-surface contract.</param>
public sealed record WorldDiagnosticCollider(FixedStaticCollider Collider, string Stamp, string Creation, string Node,
    string Operation, double Margin, bool Opaque = true) {
    /// <summary>Computed maximum inward approximation error in metres, zero for an exact contact.</summary>
    public double ConservativeInset { get; init; }
}
