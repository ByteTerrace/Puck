using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    // What this ledger holds of one source namespace, kept as each table changes, so a question about one source reads
    // that source's entry and never a whole table. Guest is the door's decision that the source is a guest's session:
    // it is recorded when the session's first reservation is admitted, kept until the session is forgotten,
    // checkpointed, and carried by every crossing record and taped arrival of the session. Reclamation and the guest
    // budgets read it and never the boundary, so an escrow with no boundary (a replay) keeps the same ledger as the
    // world it replays.
    private sealed class SourceLedger {
        // The transfer ids of the source's keys that hold a row.
        public readonly HashSet<ulong> Keys = [];
        // The travelers the source holds a credential for.
        public readonly HashSet<WorldEntityAddress> Travelers = [];
        // Its live leases, retained commits and uncertain arrivals.
        public int Held;
        // The traveler incarnations its live leases hold.
        public int MobilityLeases;
        public bool Guest;

        public bool IsEmpty => ((Keys.Count == 0) && (Travelers.Count == 0) && (Held == 0) && (MobilityLeases == 0));
    }
    // What every guest session holds together. The group's own worlds are bounded without it.
    private struct GuestTotals {
        // Keys and floors.
        public int Rows;
        public int Held;
        public int Credentials;
        public int MobilityLeases;
    }

    private readonly Dictionary<string, SourceLedger> m_sources = new(comparer: StringComparer.Ordinal);
    // The source namespaces that hold a credential for each traveler incarnation: one for a traveler that entered
    // once, one more for each authority it came back from.
    private readonly Dictionary<WorldEntityAddress, List<string>> m_incarnationSources = new();
    // The traveler each peer principal landed as. A peer principal names one landing, so one traveler.
    private readonly Dictionary<Principal, WorldEntityAddress> m_peerIncarnations = new();
    private GuestTotals m_guests;
    // The keys that hold a row, over every source.
    private int m_keyRows;

    private bool IsGuest(string sourceAuthority) => (m_sources.TryGetValue(key: sourceAuthority, value: out var ledger) && ledger.Guest);
    private bool HoldsRow(WorldTransferKey key) => (m_sources.TryGetValue(key: key.SourceAuthority, value: out var ledger) && ledger.Keys.Contains(item: key.TransferId));
    private SourceLedger Source(string sourceAuthority) {
        if (!m_sources.TryGetValue(key: sourceAuthority, value: out var ledger)) {
            m_sources.Add(key: sourceAuthority, value: (ledger = new SourceLedger()));
        }
        return ledger;
    }
    // A source that is no guest's and holds nothing keeps no entry. A guest's entry goes when its session is forgotten
    // (ForgetSource), which is also what drops its mark.
    private void Settle(string sourceAuthority, SourceLedger ledger) {
        if (!ledger.Guest && ledger.IsEmpty) {
            _ = m_sources.Remove(key: sourceAuthority);
        }
    }
    // Moves one source's held transactions and leased incarnations, and the guests' totals with them.
    private void Tally(string sourceAuthority, int held = 0, int mobilityLeases = 0) {
        var ledger = Source(sourceAuthority: sourceAuthority);

        ledger.Held += held;
        ledger.MobilityLeases += mobilityLeases;
        if (ledger.Guest) {
            m_guests.Held += held;
            m_guests.MobilityLeases += mobilityLeases;
        }
        Settle(ledger: ledger, sourceAuthority: sourceAuthority);
    }
    // Records the door's decision that a source is a guest's session, moving what it already holds into the guests'
    // totals.
    private void MarkGuest(string sourceAuthority) {
        var ledger = Source(sourceAuthority: sourceAuthority);

        if (ledger.Guest) {
            return;
        }
        ledger.Guest = true;
        m_guests.Rows += (ledger.Keys.Count + (m_floors.ContainsKey(key: sourceAuthority) ? 1 : 0));
        m_guests.Held += ledger.Held;
        m_guests.Credentials += ledger.Travelers.Count;
        m_guests.MobilityLeases += ledger.MobilityLeases;
    }
    // Writes one traveler credential, keeping its source's entry and both indexes with it.
    private void SetCredential(string sourceAuthority, WorldEntityAddress incarnation, MobilityAdmission admission) {
        if (m_mobilityAdmissions.TryGetValue(key: (sourceAuthority, incarnation), value: out var prior)) {
            if (prior.Principal != admission.Principal) {
                ForgetPeer(incarnation: incarnation, principal: prior.Principal);
            }
        } else {
            var ledger = Source(sourceAuthority: sourceAuthority);

            _ = ledger.Travelers.Add(item: incarnation);
            if (ledger.Guest) { m_guests.Credentials++; }
            if (!m_incarnationSources.TryGetValue(key: incarnation, value: out var sources)) {
                m_incarnationSources.Add(key: incarnation, value: (sources = new List<string>(capacity: 1)));
            }
            sources.Add(item: sourceAuthority);
        }
        m_mobilityAdmissions[(sourceAuthority, incarnation)] = admission;
        if (admission.Principal.Kind == PrincipalKind.Peer) {
            m_peerIncarnations[admission.Principal] = incarnation;
        }
    }
    private void RemoveCredential(string sourceAuthority, WorldEntityAddress incarnation) {
        if (!m_mobilityAdmissions.Remove(key: (sourceAuthority, incarnation), value: out var admission)) {
            return;
        }
        if (m_sources.TryGetValue(key: sourceAuthority, value: out var ledger) && ledger.Travelers.Remove(item: incarnation)) {
            if (ledger.Guest) { m_guests.Credentials--; }
            Settle(ledger: ledger, sourceAuthority: sourceAuthority);
        }
        if (m_incarnationSources.TryGetValue(key: incarnation, value: out var sources) && sources.Remove(item: sourceAuthority) && (sources.Count == 0)) {
            _ = m_incarnationSources.Remove(key: incarnation);
            ForgetPeer(incarnation: incarnation, principal: admission.Principal);
        }
    }
    private void ForgetPeer(Principal principal, WorldEntityAddress incarnation) {
        if (m_peerIncarnations.TryGetValue(key: principal, value: out var landed) && (landed == incarnation)) {
            _ = m_peerIncarnations.Remove(key: principal);
        }
    }
    // Rebuilds every per-source entry and index from the tables a restore just filled. The restore entered each row
    // through EnterRow, so only the marks, the held counts and the credentials remain to derive.
    private void RebuildSources(IReadOnlyList<string> guests) {
        var admissions = m_mobilityAdmissions.ToArray();

        m_incarnationSources.Clear();
        m_peerIncarnations.Clear();
        m_mobilityAdmissions.Clear();
        foreach (var guest in guests) { MarkGuest(sourceAuthority: guest); }
        foreach (var key in m_leases.Keys) { Tally(held: 1, sourceAuthority: key.SourceAuthority); }
        foreach (var key in m_committed) { Tally(held: 1, sourceAuthority: key.SourceAuthority); }
        foreach (var key in m_uncertain.Keys) { Tally(held: 1, sourceAuthority: key.SourceAuthority); }
        foreach (var lease in m_mobilityLeases.Values) { Tally(mobilityLeases: 1, sourceAuthority: lease.Transfer.SourceAuthority); }
        foreach (var (key, admission) in admissions) {
            SetCredential(admission: admission, incarnation: key.Incarnation, sourceAuthority: key.SourceAuthority);
        }
    }
}
