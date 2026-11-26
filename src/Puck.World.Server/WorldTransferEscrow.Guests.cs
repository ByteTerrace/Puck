using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    // The closed rewind group this authority belongs to, or null for a world outside any. Installed once, before
    // admission. It decides the caller of every live crossing operation (Admit) and sizes the guests' budgets; what a
    // ledger keeps of a guest afterwards is read from the ledger's own mark (SourceLedger.Guest).
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
    /// guest's traveler keeps its credential from its commit until its body is retired, here or at the world of the
    /// group it walked on to (<see cref="IWorldTransferForwarder.RetireTraveler"/>), so a missing one is never a
    /// traveler still on its way. A world outside any closed group answers <see langword="false"/>.</summary>
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
        (m_sources.TryGetValue(key: sourceAuthority, value: out var ledger) && !ledger.IsEmpty) ||
        m_floors.ContainsKey(key: sourceAuthority)
    );
    // Whether a source holds anything live: a lease (a record in flight keeps its lease), a retained or uncertain
    // commit, or a landed traveler's credential. A source with only settled keys and a floor holds nothing live.
    private bool HoldsLive(string sourceAuthority) => (
        m_sources.TryGetValue(key: sourceAuthority, value: out var ledger) &&
        ((ledger.Held > 0) || (ledger.Travelers.Count > 0))
    );
    /// <summary>Drops every row of a guest's session once the session holds nothing live here: its settled keys, its
    /// floor and its mark. A session this ledger holds nothing of is one it never saw, so its next reservation is a
    /// first entry and every other question about it answers <see cref="WorldTransferStatus.Ended"/>. Every traveler of
    /// the session was retired before its last live row went, so nothing remains to duplicate. Whether a source is a
    /// guest's session is the door's decision, recorded in this ledger when the session's first reservation was
    /// admitted; a world of the group, and any source of a world outside a closed group, was never marked and keeps
    /// its ledger.</summary>
    /// <param name="sourceAuthority">The source namespace.</param>
    internal void ReclaimGuest(string sourceAuthority) {
        if (IsGuest(sourceAuthority: sourceAuthority) && !HoldsLive(sourceAuthority: sourceAuthority)) {
            ForgetSource(sourceAuthority: sourceAuthority);
        }
    }
    // Removes every row of a source, live or settled, and its entry: its leases, retained commits, uncertain arrivals,
    // settled keys, floor and traveler credentials. Every lease and retained commit holds a row, so the source's own
    // rows name them all; an uncertain arrival outlives a restore that clears the rows, and there are never more of
    // those than one activation latched on.
    private void ForgetSource(string sourceAuthority) {
        if (m_sources.TryGetValue(key: sourceAuthority, value: out var ledger)) {
            foreach (var id in ledger.Keys.ToArray()) {
                var key = new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: id);

                ReleaseLease(key: key);
                RetireCommittedTransaction(key: key);
                LeaveRow(key: key);
            }
            foreach (var incarnation in ledger.Travelers.ToArray()) {
                RemoveCredential(incarnation: incarnation, sourceAuthority: sourceAuthority);
            }
        }
        foreach (var key in m_uncertain.Keys.Where(predicate: key => string.Equals(a: key.SourceAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)).ToArray()) {
            ReleaseUncertain(key: key);
        }
        if (m_floors.Remove(key: sourceAuthority) && IsGuest(sourceAuthority: sourceAuthority)) {
            m_guests.Rows--;
        }
        _ = m_sources.Remove(key: sourceAuthority);
    }
    /// <summary>Ends everything this ledger holds of an authority outside a closed rewind group: every traveler whose
    /// incarnation was minted outside it, whatever source handed it on, and every row of a source outside it. A
    /// restore of a rewind baseline runs it after the bodies those rows described were removed, so the restored ledger
    /// owes nothing to anyone the group does not restore with. A transfer between two worlds of the group that carried
    /// such a traveler keeps its key and its other travelers, and loses that traveler from every table, its retained
    /// commit included.</summary>
    /// <param name="group">The group's authority namespaces.</param>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    internal void EndOutside(IReadOnlySet<string> group) {
        ArgumentNullException.ThrowIfNull(argument: group);
        var incarnations = new HashSet<WorldEntityAddress>();

        foreach (var incarnation in m_incarnationSources.Keys) { _ = incarnations.Add(item: incarnation); }
        foreach (var incarnation in m_mobilityLeases.Keys) { _ = incarnations.Add(item: incarnation); }
        foreach (var incarnation in m_latestCommittedTransfer.Keys) { _ = incarnations.Add(item: incarnation); }
        foreach (var incarnation in incarnations) {
            if (!group.Contains(item: incarnation.Authority)) {
                DropCommittedMember(incarnation: incarnation);
                RetireIncarnation(incarnation: incarnation);
            }
        }
        foreach (var source in m_sources.Keys.Concat(second: m_floors.Keys).Distinct(comparer: StringComparer.Ordinal).ToArray()) {
            if (!group.Contains(item: source)) {
                ForgetSource(sourceAuthority: source);
            }
        }
        PruneDepartedAdmissions();
    }
    // Takes one traveler out of the commit its latest landing here is retained under: its member, its landed id and its
    // principal leave the retry tables, so a commit that also carried a traveler of the group keeps only that one's.
    // The traveler's credential names the principal it landed as, which is how its place in the commit is found.
    private void DropCommittedMember(WorldEntityAddress incarnation) {
        if (
            !m_latestCommittedTransfer.TryGetValue(key: incarnation, value: out var key) ||
            !m_committedPrincipals.TryGetValue(key: key, value: out var principals) ||
            !m_mobilityAdmissions.TryGetValue(key: (key.SourceAuthority, incarnation), value: out var admission)
        ) {
            return;
        }
        var ordinal = Array.IndexOf(array: principals, value: admission.Principal);

        if (ordinal < 0) {
            return;
        }
        m_committedPrincipals[key] = [.. principals.Where(predicate: (_, index) => (index != ordinal))];
        if (!m_committedLanded.TryGetValue(key: key, value: out var landed) || (ordinal >= landed.Length)) {
            return;
        }
        var id = landed[ordinal];

        m_committedLanded[key] = [.. landed.Where(predicate: (_, index) => (index != ordinal))];
        if (m_committedMembers.TryGetValue(key: key, value: out var members)) {
            m_committedMembers[key] = [.. members.Where(predicate: (member, index) => (((member.MemberId < 0) ? index : member.MemberId) != id))];
        }
    }
}
