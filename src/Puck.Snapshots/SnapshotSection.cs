namespace Puck.Snapshots;

/// <summary>
/// One component's byte range within a snapshot's flat data buffer — the section table a hash-divergence localizer walks
/// to turn "byte 41287 differs" into "the <c>bus</c> component, offset 8192 within it". Metadata only: it describes
/// positions the writer already visited, and carries none of the serialized bytes itself, so recording it never changes
/// the snapshot format or its byte count.
/// </summary>
/// <param name="Name">The component's name, in the fixed save/restore order.</param>
/// <param name="Offset">The section's starting byte offset within the snapshot's data.</param>
/// <param name="Length">The section's length in bytes.</param>
public readonly record struct SnapshotSection(string Name, int Offset, int Length);
