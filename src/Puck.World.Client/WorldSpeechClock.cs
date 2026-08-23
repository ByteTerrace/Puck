namespace Puck.World.Client;

/// <summary>The presentation clock every speech path stamps — a chat line, a whisper, a dialogue line, a live
/// voice — so <c>OverlayPredicate.Speaking</c> and <c>WorldAnchor.RecentSpeaker</c> read one fact whatever produced
/// it. Per body, the completed simulation tick it last spoke on (0 means never); plus the most recent speaker
/// overall. Presentation-only: nothing here enters the simulation.</summary>
public sealed class WorldSpeechClock {
    private readonly ulong[] m_lastSpokeTick = new ulong[WorldClient.EntityCapacity];

    /// <summary>The body that most recently spoke, or -1 when nothing has.</summary>
    public int RecentSpeakerBody { get; private set; } = -1;
    /// <summary>The completed tick the most recent speaker spoke on (0 until something has).</summary>
    public ulong RecentSpeakerTick { get; private set; }

    /// <summary>The completed tick <paramref name="bodyIndex"/> last spoke on, or 0 when it never has (or the index
    /// is out of range).</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    public ulong LastSpokeTick(int bodyIndex) => ((((uint)bodyIndex) < ((uint)m_lastSpokeTick.Length))
        ? m_lastSpokeTick[bodyIndex]
        : 0UL
    );
    /// <summary>Stamps that <paramref name="bodyIndex"/> spoke on <paramref name="tick"/>. An out-of-range index
    /// stamps nothing; a tick of 0 is indistinguishable from never, so callers on a fresh world stamp 1 or later.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    /// <param name="tick">The completed simulation tick.</param>
    public void NoteSpoke(int bodyIndex, ulong tick) {
        if (((uint)bodyIndex) >= ((uint)m_lastSpokeTick.Length)) {
            return;
        }

        m_lastSpokeTick[bodyIndex] = tick;

        if (tick >= RecentSpeakerTick) {
            RecentSpeakerBody = bodyIndex;
            RecentSpeakerTick = tick;
        }
    }
}
