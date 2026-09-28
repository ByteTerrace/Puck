namespace Puck.World.Protocol;

/// <summary>
/// Names one live document an authority installed: the activation of the server that installed it and the ordinal
/// of the install within that activation. The server mints <see cref="Activation"/> once per construction, so a
/// restarted, restored or recreated world never shares one with the world it replaced, and bumps
/// <see cref="Sequence"/> at every install of a new live document (an applied mutation, an undo, a reload, a rule
/// publication). Every delivered document carries the version it reflects and every mutation verdict the version it
/// applied at, so a reader orders a verdict against a delivered document exactly, whatever order they arrive in.
/// <para>Delivery metadata, never simulation state: no hash, checkpoint or replay reads it.</para>
/// </summary>
/// <param name="Activation">The installing server's activation; <see cref="Guid.Empty"/> for a document no authority
/// has delivered yet (a placeholder).</param>
/// <param name="Sequence">The install ordinal within <paramref name="Activation"/>, zero for the boot document.</param>
public readonly record struct WorldDocumentVersion(Guid Activation, long Sequence) {
    /// <summary>Gets whether this names a document an authority delivered, rather than a placeholder.</summary>
    public bool IsDelivered => (Activation != Guid.Empty);

    /// <summary>Returns whether a document at this version already reflects what was installed at
    /// <paramref name="other"/>: the same activation, at or past its sequence.</summary>
    /// <param name="other">The version to compare against, typically a verdict's.</param>
    /// <returns><see langword="true"/> when this version covers <paramref name="other"/>.</returns>
    public bool Covers(in WorldDocumentVersion other) => ((Activation == other.Activation) && (Sequence >= other.Sequence));
}
