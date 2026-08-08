namespace Puck.Scripting;

/// <summary>One decoded 32-byte output cell (guest → host).</summary>
/// <param name="Kind">The output cell kind — <c>Act</c> or <c>Ask</c>.</param>
/// <param name="Channel">The descriptor index into the guest's declared channel table.</param>
/// <param name="HandleIndex">The subject handle an <c>Act</c> acts through; reserved-must-be-zero on an <c>Ask</c>.</param>
/// <param name="HandleGeneration">The handle generation paired with <paramref name="HandleIndex"/>; reserved-must-be-zero on an <c>Ask</c>.</param>
/// <param name="Verb">The 0-based channel-relative operation ordinal on an <c>Act</c>, or the
/// <see cref="AddonSubjectKind"/> discriminant on an <c>Ask</c>. On an input-channel <c>Act</c>, the low
/// <see cref="AddonAbi.InputVerbReservedBits"/> bits are required-zero — a nonzero value is a protocol fault —
/// and the remaining bits are the declared channel-name ordinal; validating the ordinal and its payload shape
/// is the Simulation adapter's vocabulary layer, not this core's structural decode.</param>
/// <param name="A">The primary payload lane.</param>
/// <param name="B">The secondary payload lane.</param>
/// <param name="C">The tertiary payload lane.</param>
public readonly record struct AddonOutCell(AddonOutCellKind Kind, byte Channel, ushort HandleIndex, ushort HandleGeneration, ushort Verb, long A, long B, long C);
