namespace Puck.GamingBricks.Forge;

/// <summary>
/// The label bookkeeping every machine-code emitter in the forge shares, whatever instruction set it encodes: an id
/// allocated before its target is known (<see cref="New"/>), bound to a byte offset once the stream reaches that
/// target (<see cref="Mark"/>), and read back at fixup time (<see cref="Resolve"/>) — including the refusal a branch
/// naming a label nothing ever bound must produce, rather than silently patching a zero offset. The fixup lists
/// themselves stay with the emitter: they carry per-instruction-set patch encodings and reach lengths, and only the
/// id-to-offset mapping is common.
/// </summary>
public sealed class LabelTable {
    private readonly Dictionary<int, int> m_offsets = [];

    private int m_nextLabel;

    /// <summary>Allocates an unbound label id; bind it with <see cref="Mark"/> at the target instruction.</summary>
    /// <returns>The new label id.</returns>
    public int New() =>
        m_nextLabel++;
    /// <summary>Binds <paramref name="label"/> to a byte offset in the emitter's stream.</summary>
    /// <param name="label">The label id to bind.</param>
    /// <param name="offset">The byte offset in the emitted stream that the label names.</param>
    public void Mark(int label, int offset) =>
        m_offsets[label] = offset;
    /// <summary>Resolves a bound label to the byte offset it names.</summary>
    /// <param name="kind">The branch family named in the refusal — the emitter's own mnemonic for the fixup being
    /// resolved, so the message points at the instruction that cannot be patched.</param>
    /// <param name="label">The label id the fixup targets.</param>
    /// <returns>The byte offset <paramref name="label"/> was bound to.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="label"/> was never bound.</exception>
    public int Resolve(string kind, int label) {
        if (!m_offsets.TryGetValue(
            key: label,
            value: out var target
        )) {
            throw new InvalidOperationException(message: $"{kind} targets an unbound label {label}.");
        }

        return target;
    }
}
