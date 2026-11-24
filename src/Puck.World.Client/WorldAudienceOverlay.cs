namespace Puck.World.Client;

/// <summary>One audience a world's own scene draws beside its own viewers: a viewer whose stream shares the world's whole
/// scene except the placements its disclosure varies (<see cref="WorldPlacementAudiences.Varies"/>), whose view of the
/// world's residency (<c>SdfViewSnapshot.Audience</c>) draws the neutral draws and those of these placements, as the
/// viewer's own delivery deals them, tagged for <paramref name="Audience"/> alone
/// (<see cref="WorldRigidAssets.ReconcileStatic"/>).</summary>
/// <param name="Audience">The view audience, one through <see cref="Puck.SdfVm.SdfMeshAudience.MaxViewAudience"/>.</param>
/// <param name="Definition">The viewer's delivered document, whose varying placements the scene copies.</param>
public readonly record struct WorldAudienceOverlay(byte Audience, WorldDefinition Definition);
