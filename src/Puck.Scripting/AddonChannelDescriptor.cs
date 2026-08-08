namespace Puck.Scripting;

/// <summary>One decoded 16-byte channel descriptor table entry.</summary>
/// <param name="Kind">The channel kind, an ABI-pinned wire value that determines the channel's lane.</param>
/// <param name="VerbCount">The per-kind verb count or declared source count; meaning depends on <paramref name="Kind"/>.</param>
/// <param name="VerbTablePtr">The byte offset of the channel's verb table, or <c>0</c> when the kind carries none.</param>
public readonly record struct AddonChannelDescriptor(AddonChannelKind Kind, ushort VerbCount, uint VerbTablePtr);
