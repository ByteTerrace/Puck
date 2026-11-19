using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    private readonly List<(WorldGameplayCue Cue, WorldRuleHost.CueReads Reads, StateArena Arena)> m_cues = [];
    private bool m_deliveringCues;
    private byte m_cueMusicBoundary;

    internal void RecordCue(WorldGameplayCue cue, WorldRuleHost.CueReads reads, StateArena arena) => m_cues.Add((cue, reads, arena));

    // The authority image never contains cues. A delivery creates them from the live recipient's capability and
    // its already-redacted body table. A nested primer or restored timeline has no event window.
    internal WorldSnapshot PresentCues(in WorldSnapshot snapshot, WorldRecipientView view, bool fullBodies) {
        if (!m_deliveringCues || !view.Observes) { return snapshot; }

        List<WorldGameplayCue>? disclosed = null;
        IReadOnlyList<WorldPlacement>? placements = null;
        foreach (var (cue, reads, arena) in m_cues) {
            if (cue.Tick != snapshot.Tick || !ReferenceEquals(arena, Host.Arena) || (reads.AuthorityOnly && !view.IsAuthority) ||
                (reads.FullBodies && !fullBodies) || !reads.Rows.All(view.ReadsCueDependency)) { continue; }
            if (reads.Placements.Length != 0 && !reads.Placements.All(id => (placements ??= view.Placements).Any(p => p.Id == id))) { continue; }
            if (cue.Body is { } body) {
                if (!snapshot.TryFindEntity(body, cue.Generation, out _)) { continue; }
            } else if (!fullBodies) { continue; }

            (disclosed ??= []).Add(cue);
            if (disclosed.Count == WorldRuleCapacity.MaxDeliveredCues) { break; }
        }
        return snapshot with { Cues = disclosed?.ToArray() ?? [], MusicBoundary = m_cueMusicBoundary,
            MusicSenses = m_musicDirector is null ? (byte)0 : DisclosedMusicSenses(snapshot, view) };
    }

    private byte DisclosedMusicSenses(in WorldSnapshot snapshot, WorldRecipientView view) {
        byte senses = 0;
        IReadOnlyList<WorldPlacement>? placements = null;
        foreach (var edge in Host.Events.Edges) {
            if (edge.Family is not (WorldEventFamily.RegionEnter or WorldEventFamily.RegionExit or WorldEventFamily.SeatJoin)) { continue; }
            if (!snapshot.TryFindEntity((int)edge.A, Host.Population.Generation((int)edge.A), out _)) { continue; }
            if (edge.Family is WorldEventFamily.RegionEnter or WorldEventFamily.RegionExit &&
                !(placements ??= view.Placements).Any(placement => placement.Id == edge.GateA.Id)) { continue; }
            senses |= (byte)(1 << (int)edge.Family);
        }
        return senses;
    }

}
