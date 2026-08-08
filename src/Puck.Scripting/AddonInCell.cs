namespace Puck.Scripting;

/// <summary>One input cell (host → guest), 32 bytes on the wire.</summary>
/// <param name="Kind">The input cell kind — <c>Tick</c>, <c>Answer</c>, or <c>Observation</c>.</param>
/// <param name="Channel">The channel this cell belongs to.</param>
/// <param name="Ordinal">On an <see cref="AddonInCellKind.Answer"/>, which output cell of the guest's previous batch this answers.</param>
/// <param name="HandleIndex">A granted handle on an <see cref="AddonInCellKind.Answer"/>; the observed subject on an <see cref="AddonInCellKind.Observation"/>.</param>
/// <param name="HandleGeneration">The handle generation paired with <paramref name="HandleIndex"/>.</param>
/// <param name="Verdict">The authorization outcome, as data; <see cref="AddonVerdict.None"/> on kinds that carry none.</param>
/// <param name="Verb">The channel-relative operation ordinal, or a multi-part answer's 0-based part index.</param>
/// <param name="A">The primary payload lane.</param>
/// <param name="B">The secondary payload lane.</param>
public readonly record struct AddonInCell(AddonInCellKind Kind, byte Channel, ushort Ordinal, ushort HandleIndex, ushort HandleGeneration, AddonVerdict Verdict, byte Verb, long A, long B);
