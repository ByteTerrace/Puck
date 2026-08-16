namespace Puck.World;

/// <summary>
/// The <c>water</c> section — the world's standing-water MEDIUM: one horizontal free surface at a world-space
/// altitude. The section describes the WORLD's medium, never a mover's tuning: how a body behaves inside or against
/// the water belongs to that kit's own tuning row. Authored data only at boot; the swim motion model's phase-4 stage
/// (<see cref="BodyMotionOp.ApplyBuoyancyAndSurface"/>) is its live consumer, reading the compiled waterline from the
/// population — <c>world.status</c> echoes the authored level regardless.
/// </summary>
/// <remarks>Bounded water BODIES (volumes) are the destination shape; a future optional member widens this record
/// without moving <see cref="Level"/>, so a level-only document keeps its meaning: one infinite surface.</remarks>
/// <param name="Level">The waterline's world-space Y — the free surface of the world's standing water. A position
/// below it is submerged.</param>
public sealed record WorldWaterSection(float Level);
