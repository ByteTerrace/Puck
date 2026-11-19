using System.Numerics;

namespace Puck.World.Protocol;

/// <summary>A cue with its association resolved against the recipient's own delivered entity image.</summary>
public readonly record struct WorldPresentationCue(WorldGameplayCue Cue, Vector3? Site);

/// <summary>One tick of presentation events, owned by its consumer.</summary>
public readonly record struct WorldPresentationCueTick(ulong Tick, byte MusicBoundary, byte MusicSenses, WorldPresentationCue[] Cues);

/// <summary>Hands off one-shot snapshot data to a frame consumer without retaining borrowed snapshot memory.</summary>
public sealed class WorldPresentationCueFeed {
    private readonly Lock m_gate = new();
    private readonly Queue<WorldPresentationCueTick> m_pending = new();

    /// <summary>Copies the delivery's events, omitting body cues whose generation is absent.</summary>
    public static WorldPresentationCueTick Capture(in WorldSnapshot snapshot) {
        List<WorldPresentationCue>? cues = null;
        foreach (ref readonly var cue in snapshot.Cues.Span) {
            if (cue.Body is not { } body) { (cues ??= []).Add(new(cue, null)); continue; }
            foreach (ref readonly var entity in snapshot.Entries.Span) {
                if (entity.Index != body || entity.Generation != cue.Generation) { continue; }
                (cues ??= []).Add(new(cue, entity.Position));
                break;
            }
        }
        return new(snapshot.Tick, snapshot.MusicBoundary, snapshot.MusicSenses, cues?.ToArray() ?? []);
    }

    /// <summary>Queues one tick for presentation, dropping the oldest queued tick if presentation falls behind.</summary>
    public void Publish(WorldPresentationCueTick tick) {
        lock (m_gate) {
            if (m_pending.Count == 64) { m_pending.Dequeue(); }
            m_pending.Enqueue(tick);
        }
    }

    /// <summary>Takes the next queued tick exactly once.</summary>
    public bool TryTake(out WorldPresentationCueTick tick) {
        lock (m_gate) { return m_pending.TryDequeue(out tick); }
    }

    /// <summary>Discards events from an ended delivery lifetime.</summary>
    public void Clear() { lock (m_gate) { m_pending.Clear(); } }
}
