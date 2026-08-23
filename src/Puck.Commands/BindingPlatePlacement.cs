using System.Numerics;

namespace Puck.Commands;

/// <summary>One plate on a binding bar as the overlay reads it: its pitch position from the bank's anchor (x right,
/// y up, in button sizes) and its badge nudge as signed multiples of the layout's glyph offset (+1 right / up).</summary>
/// <param name="Position">The plate center, button pitches from the anchor.</param>
/// <param name="Badge">The badge nudge multiples.</param>
public readonly record struct BindingPlatePlacement(Vector2 Position, Vector2 Badge);
