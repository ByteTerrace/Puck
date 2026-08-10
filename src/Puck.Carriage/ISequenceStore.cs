namespace Puck.Carriage;

/// <summary>
/// The bearer-claim replay check seam (docs/world-model.md, "Signed carriage"): a durable per-(issuer,
/// subject) high-water sequence mark. Storage, durability, and retention-coupled-to-window are the engine's
/// problem, not this prototype's; <see cref="InMemorySequenceStore"/> exists only to make the harness
/// runnable and is not a production implementation.
/// </summary>
/// <remarks>
/// <para><b>Compare and advance are one operation, deliberately.</b> A separate read (a hypothetical
/// <c>TryGetMark</c>) and write (<c>Advance</c>) would be a check-then-act race: two receivers handling the
/// same bearer claim concurrently would both read the old mark, both find the sequence higher, and both
/// accept — the replay the mark exists to stop, arrived at through the mark itself. A single-writer note on
/// the store cannot fix it, because the race is composed by the caller across two calls, not created inside
/// the store. So there is one method, and the decision belongs to the store.</para>
/// </remarks>
public interface ISequenceStore {
    /// <summary>
    /// Atomically compares <paramref name="sequence"/> against the recorded high-water mark for an (issuer
    /// domain, subject) pair and advances the mark when it strictly exceeds it.
    /// </summary>
    /// <param name="domain">The issuing domain's root fingerprint.</param>
    /// <param name="subject">The claim's subject.</param>
    /// <param name="sequence">The sequence the claim declares.</param>
    /// <returns>
    /// <see langword="true"/> when no mark existed or <paramref name="sequence"/> was strictly above it and
    /// the mark now records <paramref name="sequence"/>; <see langword="false"/> when it did not advance,
    /// which the verifier reports as a replay.
    /// </returns>
    /// <remarks>
    /// <para>Implementations must make the compare and the advance indivisible with respect to every other
    /// caller for the same pair — a lock, a conditional/compare-and-swap update, or a transaction. Two
    /// concurrent calls carrying the same sequence must see exactly one <see langword="true"/>. A durable
    /// implementation must also have persisted the advance before returning <see langword="true"/>: a mark
    /// lost to a crash after the claim was admitted reopens the replay it just refused.</para>
    /// <para><b>Deny by default when the store cannot decide.</b> A store that is unreachable, cannot read
    /// its mark, or cannot durably record the advance must not return <see langword="true"/>. It may return
    /// <see langword="false"/> or raise; <see cref="CarriageVerifier"/> refuses either way, and never lets
    /// the failure propagate to its caller (docs/signed-carriage-wire.md §8). The reading this forecloses is
    /// "accept because the store is down", which admits exactly the replay the mark exists to refuse.</para>
    /// </remarks>
    bool TryAdvance(string domain, string subject, ulong sequence);
}

/// <summary>An in-process <see cref="ISequenceStore"/> for the harness. Not durable — a real receiver's mark must survive a restart — but it is atomic, because the interface's contract is not optional.</summary>
public sealed class InMemorySequenceStore : ISequenceStore {
    private readonly Dictionary<(string Domain, string Subject), ulong> m_marks = [];

    /// <inheritdoc/>
    public bool TryAdvance(string domain, string subject, ulong sequence) {
        lock (m_marks) {
            if (m_marks.TryGetValue(key: (domain, subject), out var mark) && (sequence <= mark)) {
                return false;
            }

            m_marks[(domain, subject)] = sequence;

            return true;
        }
    }
}
