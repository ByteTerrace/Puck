using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    private readonly List<(WorldGameplayCue Cue, WorldRuleHost.CueReads Reads, StateArena Arena)> m_cues = [];
    private CueAdmission[] m_localAdmissions = [];
    private bool m_deliveringCues;
    private byte m_cueMusicBoundary;

    internal void RecordCue(WorldGameplayCue cue, WorldRuleHost.CueReads reads, StateArena arena) => m_cues.Add((cue, reads, arena));

    // The authority image never contains cues. A delivery creates them from the live recipient's capability and
    // its already-redacted body table. A nested primer or restored timeline has no event window.
    internal WorldSnapshot PresentCues(in WorldSnapshot snapshot, WorldRecipientView view, bool fullBodies, WorldBodyView? bodies = null) {
        if (!m_deliveringCues || !view.Observes) { return snapshot; }

        List<WorldGameplayCue>? disclosed = null;
        var admission = new CueAdmission(view, fullBodies);
        foreach (var (cue, reads, arena) in m_cues) {
            if (!IsCurrent(snapshot, cue, arena) || !admission.Admits(reads, Host.RuleHost) ||
                ((cue.Body is { } body) && !(bodies?.Contains(body, cue.Generation) ?? snapshot.TryFindEntity(body, cue.Generation, out _)))) { continue; }

            (disclosed ??= []).Add(cue);
            if (disclosed.Count == WorldRuleCapacity.MaxDeliveredCues) { break; }
        }
        return snapshot with { Cues = disclosed?.ToArray() ?? [], MusicBoundary = m_cueMusicBoundary,
            MusicSenses = m_musicDirector is null ? (byte)0 : DisclosedMusicSenses(snapshot, view, bodies) };
    }

    // The in-process presentation serves every local seat from one delivery, so each cue carries the seats whose own
    // views admit it (WorldGameplayCue.Audience) and each consumer presents it for the seats it serves: the listener
    // hears what its seat may be told. The delivery carries every body, so the body tests read the whole table.
    internal WorldSnapshot PresentLocalCues(in WorldSnapshot snapshot, IReadOnlyList<WorldRecipientView> seats) {
        if (!m_deliveringCues) { return snapshot; }
        var count = Math.Min(seats.Count, WorldGameplayCue.MaxAudienceSeats);
        if (m_localAdmissions.Length < count) { m_localAdmissions = new CueAdmission[count]; }
        for (var seat = 0; seat < count; seat++) { m_localAdmissions[seat] = new(seats[seat], fullBodies: true); }

        List<WorldGameplayCue>? disclosed = null;
        foreach (var (cue, reads, arena) in m_cues) {
            if (!IsCurrent(snapshot, cue, arena) || ((cue.Body is { } body) && !snapshot.TryFindEntity(body, cue.Generation, out _))) { continue; }
            var audience = 0UL;
            for (var seat = 0; seat < count; seat++) {
                if (seats[seat].Observes && m_localAdmissions[seat].Admits(reads, Host.RuleHost)) { audience |= 1UL << seat; }
            }
            if (audience == 0UL) { continue; }

            (disclosed ??= []).Add(cue with { Audience = audience });
            if (disclosed.Count == WorldRuleCapacity.MaxDeliveredCues) { break; }
        }
        byte senses = 0;
        if (m_musicDirector is not null) {
            for (var seat = 0; seat < count; seat++) {
                if (seats[seat].Observes) { senses |= DisclosedMusicSenses(snapshot, seats[seat]); }
            }
        }
        Array.Clear(m_localAdmissions);
        return snapshot with { Cues = disclosed?.ToArray() ?? [], MusicBoundary = m_cueMusicBoundary, MusicSenses = senses };
    }

    private bool IsCurrent(in WorldSnapshot snapshot, in WorldGameplayCue cue, StateArena arena) =>
        cue.Tick == snapshot.Tick && ReferenceEquals(arena, Host.Arena);

    // One recipient's verdicts over a delivery's cues. A rule's cues share one CueReads, so the last verdict is kept;
    // the placement projection and the field-region verdict are read at most once, and only when a cue needs them.
    private struct CueAdmission(WorldRecipientView view, bool fullBodies) {
        private IReadOnlyList<WorldPlacement>? m_placements;
        private bool? m_fields;
        private WorldRuleHost.CueReads? m_last;
        private bool m_lastVerdict;

        public bool Admits(WorldRuleHost.CueReads reads, WorldRuleHost host) {
            if (ReferenceEquals(reads, m_last)) { return m_lastVerdict; }
            m_last = reads;
            m_lastVerdict = Decide(reads);
            if (!m_lastVerdict && reads.Undeclared is not null && !view.IsAuthority) { host.NarrateUndeclaredCue(reads); }
            return m_lastVerdict;
        }

        private bool Decide(WorldRuleHost.CueReads reads) {
            if ((reads.Undeclared is not null && !view.IsAuthority) || (reads.FullBodies && !fullBodies)) { return false; }
            foreach (var row in reads.Rows) {
                if (!view.ReadsCueDependency(row)) { return false; }
            }
            foreach (var screen in reads.Screens) {
                if (!view.HoldsMachine(screen)) { return false; }
            }
            if (reads.Fields && !(m_fields ??= view.ReadsEveryFieldRegion())) { return false; }
            if (reads.Placements.Length != 0) {
                var placements = m_placements ??= view.Placements;
                foreach (var id in reads.Placements) {
                    if (!placements.Any(placement => placement.Id == id)) { return false; }
                }
            }
            return true;
        }
    }

    private byte DisclosedMusicSenses(in WorldSnapshot snapshot, WorldRecipientView view, WorldBodyView? bodies = null) {
        byte senses = 0;
        IReadOnlyList<WorldPlacement>? placements = null;
        foreach (var edge in Host.Events.Edges) {
            if (edge.Family is not (WorldEventFamily.RegionEnter or WorldEventFamily.RegionExit or WorldEventFamily.SeatJoin)) { continue; }
            var body = (int)edge.A;
            var generation = Host.Population.Generation(body);
            if (!(bodies?.Contains(body, generation) ?? snapshot.TryFindEntity(body, generation, out _))) { continue; }
            if (edge.Family is WorldEventFamily.RegionEnter or WorldEventFamily.RegionExit &&
                !(placements ??= view.Placements).Any(placement => placement.Id == edge.GateA.Id)) { continue; }
            senses |= (byte)(1 << (int)edge.Family);
        }
        return senses;
    }

}
