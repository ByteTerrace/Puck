namespace Puck.Commands;

/// <summary>
/// One ring slot in a <see cref="TickTranscript"/>: the commands dispatched during a tick, bracketed by an
/// optional before/after state-hash sample. Reused in place across ring wraps (its command list is a
/// fixed-capacity array allocated once, at construction) so reading it costs nothing beyond the read itself — do
/// not hold a reference across a later <see cref="TickTranscript.RecordTick"/> call on the SAME transcript, since
/// that call may reuse this exact instance for a different tick once the ring wraps.
/// </summary>
public sealed class TickTranscriptEntry {
    private readonly string[] m_commands = new string[TickTranscript.MaxCommandsPerTick];

    /// <summary>Gets the tick number this entry describes.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Gets the state hash sampled before the tick stepped, or <see langword="null"/> when the caller
    /// did not supply one.</summary>
    public ulong? HashBefore { get; private set; }

    /// <summary>Gets the state hash sampled after the tick stepped, or <see langword="null"/> when the caller
    /// did not supply one.</summary>
    public ulong? HashAfter { get; private set; }

    /// <summary>Gets how many commands are recorded (at most <see cref="TickTranscript.MaxCommandsPerTick"/>).</summary>
    public int CommandCount { get; private set; }

    /// <summary>Gets how many additional commands were dispatched this tick beyond <see cref="CommandCount"/> —
    /// counted, not stored, so one runaway tick cannot grow the buffer.</summary>
    public int OverflowCount { get; private set; }

    /// <summary>Gets the command at <paramref name="index"/> (0-based, insertion order).</summary>
    /// <param name="index">The command's index, in <c>[0, CommandCount)</c>.</param>
    public string CommandAt(int index) => m_commands[index];

    // Appends one command's text, or counts it as overflow once the fixed slot list is full.
    internal void AddCommand(string text) {
        if (CommandCount < m_commands.Length) {
            m_commands[CommandCount] = text;
            CommandCount++;
        } else {
            OverflowCount++;
        }
    }

    // Resets to an empty pending bucket (called right after a Seal copies this entry elsewhere).
    internal void Clear() {
        CommandCount = 0;
        OverflowCount = 0;
    }

    // Overwrites this slot with a sealed tick's data, copied from the transcript's pending bucket.
    internal void Seal(ulong tick, ulong? hashBefore, ulong? hashAfter, TickTranscriptEntry pending) {
        Tick = tick;
        HashBefore = hashBefore;
        HashAfter = hashAfter;
        CommandCount = pending.CommandCount;
        OverflowCount = pending.OverflowCount;

        Array.Copy(sourceArray: pending.m_commands, destinationArray: m_commands, length: pending.CommandCount);
    }
}
