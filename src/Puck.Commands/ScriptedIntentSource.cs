namespace Puck.Commands;

/// <summary>
/// An <see cref="IIntentSource{TIntent}"/> whose intents come from a pure function of <c>(tick, slot)</c> — no
/// hardware, no clock. Used to drive a determinism/replay self-check with a fixed, reproducible input sequence.
/// </summary>
/// <typeparam name="TIntent">The per-participant intent value the simulation steps.</typeparam>
public sealed class ScriptedIntentSource<TIntent> : IIntentSource<TIntent> {
    private readonly Func<ulong, int, TIntent> m_script;

    /// <summary>Initializes the source from a deterministic script that maps a tick and slot to that participant's intent.</summary>
    public ScriptedIntentSource(Func<ulong, int, TIntent> script) {
        ArgumentNullException.ThrowIfNull(script);

        m_script = script;
    }

    /// <inheritdoc/>
    public void BeginFrame(ulong firstTick) { }

    /// <inheritdoc/>
    public TIntent[] CollectTick(ulong tick, IReadOnlyList<Guid> participants) {
        var intents = new TIntent[participants.Count];

        for (var slot = 0; (slot < participants.Count); slot++) {
            intents[slot] = m_script(arg1: tick, arg2: slot);
        }

        return intents;
    }
}
