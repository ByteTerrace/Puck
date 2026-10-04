namespace Puck.World.Client;

/// <summary>Session-only sky auditioning by the zero-based rows printed by world.lighting.</summary>
public sealed class WorldSkyAudition {
    private readonly HashSet<int> m_muted = [];

    private int m_solo = -1;

    /// <summary>Gets the solo row, or -1 for the complete stack.</summary>
    public int Solo => m_solo;
    /// <summary>Gets the revision consumed by the environment resolver.</summary>
    public int Revision { get; private set; }

    /// <summary>Reports whether a row is muted.</summary>
    public bool Muted(int index) => m_muted.Contains(index);
    /// <summary>Reports whether a row contributes, with mute taking precedence over solo.</summary>
    public bool Includes(int index) => (((m_solo < 0) || (m_solo == index)) && !Muted(index));
    /// <summary>Solos a row, or restores the stack with -1.</summary>
    public void SetSolo(int index) { m_solo = index; Revision++; }
    /// <summary>Mutes or restores one row.</summary>
    public void SetMuted(int index, bool muted) {
        if (muted) { m_muted.Add(index); } else { m_muted.Remove(index); }
        Revision++;
    }
}
