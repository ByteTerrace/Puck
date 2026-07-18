using Puck.Commands;

namespace Puck.Scripting;

/// <summary>One decoded, neutral virtual-pad command record. The <see cref="Kind"/> is derived from
/// <see cref="PadId"/> at decode time; every value is <see cref="Puck.Maths.FixedQ4816"/> raw <c>i64</c> bits.</summary>
/// <param name="PadId">The frozen pad vocabulary id (see <see cref="PadCommandId"/>).</param>
/// <param name="Kind">The value shape, derived from <paramref name="PadId"/>.</param>
/// <param name="Phase">The command phase (mirrors <see cref="CommandPhase"/>).</param>
/// <param name="ValueX">The primary/X fixed-point value, raw bits.</param>
/// <param name="ValueY">The Y fixed-point value, raw bits; <c>0</c> for Digital and Axis1D kinds.</param>
public readonly record struct AddonCommand(ushort PadId, AddonValueKind Kind, CommandPhase Phase, long ValueX, long ValueY);
