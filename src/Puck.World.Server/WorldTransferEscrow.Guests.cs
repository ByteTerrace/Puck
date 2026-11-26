using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    // The closed rewind group this authority belongs to, or null for a world outside any. Installed once, before
    // admission; every live crossing operation is decided against it (Admit), and a guest's ledger is kept by it.
    private WorldTransferBoundary? m_boundary;

    /// <summary>Gets the closed rewind group's boundary, or <see langword="null"/> for a world outside any.</summary>
    public WorldTransferBoundary? Boundary => m_boundary;

    /// <summary>Installs the closed rewind group's boundary. It cannot be replaced during an activation.</summary>
    /// <param name="boundary">The boundary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="boundary"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A boundary is already installed.</exception>
    internal void Constrain(WorldTransferBoundary boundary) {
        ArgumentNullException.ThrowIfNull(argument: boundary);
        if (m_boundary is not null) { throw new InvalidOperationException(message: "transfer boundary is already installed"); }
        m_boundary = boundary;
    }

    /// <summary>The crossing door: decides the caller of one live operation that can change this ledger. Reserve,
    /// commit, abort, acknowledge and status each run behind the admission it returns.</summary>
    /// <param name="caller">Who made the call, or <see langword="null"/> for a caller that proved nothing.</param>
    /// <param name="sourceAuthority">The source namespace the operation names.</param>
    /// <returns>The admission.</returns>
    public WorldTransferAdmission Admit(WorldTransferCaller? caller, string sourceAuthority) => WorldTransferAdmission.Of(
        boundary: m_boundary,
        caller: caller,
        sourceAuthority: sourceAuthority
    );
    /// <summary>Returns why a federation connection may not ask this authority a custody question (a hop request, its
    /// confirmation or settlement, a custody query), or <see langword="null"/>. A closed rewind group answers none
    /// from the network: a guest carries no title, and no other authority is admitted.</summary>
    /// <returns>The named refusal, or <see langword="null"/>.</returns>
    public string? CustodyRefusal() => ((m_boundary is null)
        ? null
        : "closed rewind group answers no custody question from outside: a player's party carries no title");

    /// <summary>Returns whether a traveler a federation connection asks after has ended here for good: the caller is
    /// outside a closed rewind group, or is a guest of one and this ledger holds no credential for the traveler. A
    /// guest's traveler keeps its credential from its commit until it is retired, at this authority and at every
    /// authority of the group it walked on to, so a missing one is never a traveler still on its way. A world outside
    /// any closed group answers <see langword="false"/>.</summary>
    /// <param name="caller">Who asks.</param>
    /// <param name="sourceAuthority">The source namespace the question names.</param>
    /// <param name="mobility">The traveler's credential.</param>
    /// <returns><see langword="true"/> when the traveler ended.</returns>
    public bool TravelerEnded(WorldTransferCaller? caller, string sourceAuthority, in WorldMobilityIdentity mobility) {
        var admission = Admit(caller: caller, sourceAuthority: sourceAuthority);

        return (admission.Refused || (admission.IsGuest && !m_mobilityAdmissions.ContainsKey(key: (sourceAuthority, mobility.Incarnation))));
    }

    // Whether this ledger holds any row of a source: a key (leased, committed, uncertain or spent), a floor or a
    // traveler credential.
    private bool HoldsSource(string sourceAuthority) => (
        m_sourceRows.ContainsKey(key: sourceAuthority) ||
        m_floors.ContainsKey(key: sourceAuthority) ||
        HoldsCredential(sourceAuthority: sourceAuthority)
    );
    // Whether a traveler this source handed over still holds a credential here.
    private bool HoldsCredential(string sourceAuthority) {
        foreach (var alias in m_mobilityAdmissions.Keys) {
            if (string.Equals(a: alias.SourceAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }
        return false;
    }
    // Whether a source holds anything live: a lease, a retained or uncertain commit, a record in flight or a landed
    // traveler's credential. A source with only settled keys and a floor holds nothing live.
    private bool HoldsLive(string sourceAuthority) {
        bool Names(WorldTransferKey key) => string.Equals(a: key.SourceAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal);

        if ((m_open is { } open) && Names(key: open.Key)) {
            return true;
        }
        foreach (var key in m_leases.Keys) { if (Names(key: key)) { return true; } }
        foreach (var key in m_committed) { if (Names(key: key)) { return true; } }
        foreach (var key in m_uncertain.Keys) { if (Names(key: key)) { return true; } }
        return HoldsCredential(sourceAuthority: sourceAuthority);
    }
    /// <summary>Drops every row of a guest's session once the session holds nothing live here: its settled keys and
    /// its floor. A session this ledger holds nothing of is one it never saw, so its next reservation is a first
    /// entry and every other question about it answers <see cref="WorldTransferStatus.Ended"/>. Every traveler of the
    /// session was retired before its last live row went, so nothing remains to duplicate. A world of the group, and
    /// any source of a world outside a closed group, keeps its ledger.</summary>
    /// <param name="sourceAuthority">The source namespace.</param>
    internal void ReclaimGuest(string sourceAuthority) {
        if (
            (m_boundary is not { } boundary) ||
            boundary.Contains(authority: sourceAuthority) ||
            HoldsLive(sourceAuthority: sourceAuthority)
        ) {
            return;
        }
        ForgetSource(sourceAuthority: sourceAuthority);
    }
    // Removes every row of a source, live or settled: its leases, retained commits, uncertain arrivals, settled keys,
    // floor and traveler credentials.
    private void ForgetSource(string sourceAuthority) {
        bool Names(WorldTransferKey key) => string.Equals(a: key.SourceAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal);

        foreach (var key in m_leases.Keys.Where(predicate: Names).ToArray()) {
            ReleaseLease(key: key);
        }
        foreach (var key in m_committed.Where(predicate: Names).ToArray()) {
            RetireCommittedTransaction(key: key);
        }
        foreach (var key in m_uncertain.Keys.Where(predicate: Names).ToArray()) {
            _ = m_uncertain.Remove(key: key);
        }
        foreach (var key in m_rows.Where(predicate: Names).ToArray()) {
            LeaveRow(key: key);
        }
        _ = m_floors.Remove(key: sourceAuthority);
        foreach (var alias in m_mobilityAdmissions.Keys.Where(predicate: candidate => string.Equals(a: candidate.SourceAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)).ToArray()) {
            _ = m_mobilityAdmissions.Remove(key: alias);
        }
    }
    /// <summary>Ends everything this ledger holds of an authority outside a closed rewind group: every traveler whose
    /// incarnation was minted outside it, whatever source handed it on, and every row of a source outside it. A
    /// restore of a rewind baseline runs it after the bodies those rows described were removed, so the restored ledger
    /// owes nothing to anyone the group does not restore with. A transfer between two worlds of the group that carried
    /// such a traveler keeps its key and loses that traveler alone.</summary>
    /// <param name="group">The group's authority namespaces.</param>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    internal void EndOutside(IReadOnlySet<string> group) {
        ArgumentNullException.ThrowIfNull(argument: group);
        var incarnations = new HashSet<WorldEntityAddress>();

        foreach (var alias in m_mobilityAdmissions.Keys) { _ = incarnations.Add(item: alias.Incarnation); }
        foreach (var incarnation in m_mobilityLeases.Keys) { _ = incarnations.Add(item: incarnation); }
        foreach (var incarnation in m_latestCommittedTransfer.Keys) { _ = incarnations.Add(item: incarnation); }
        foreach (var incarnation in incarnations) {
            if (!group.Contains(item: incarnation.Authority)) {
                RetireIncarnation(incarnation: incarnation);
            }
        }

        var sources = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var key in m_rows) { _ = sources.Add(item: key.SourceAuthority); }
        foreach (var source in m_floors.Keys) { _ = sources.Add(item: source); }
        foreach (var alias in m_mobilityAdmissions.Keys) { _ = sources.Add(item: alias.SourceAuthority); }
        foreach (var source in sources) {
            if (!group.Contains(item: source)) {
                ForgetSource(sourceAuthority: source);
            }
        }
        PruneDepartedAdmissions();
    }
}
