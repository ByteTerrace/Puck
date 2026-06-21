namespace Puck.Demo.MiniAction;

/// <summary>
/// An <see cref="IPlayerIntentSource"/> whose intents come from a pure function of <c>(tick, slot)</c> — no hardware,
/// no clock. Used to drive the determinism/replay self-check with a fixed, reproducible input sequence.
/// </summary>
public sealed class ScriptedIntentSource : IPlayerIntentSource {
    private readonly Func<ulong, int, PlayerIntent> m_script;

    /// <summary>Initializes the source from a deterministic script that maps a tick and slot to that player's intent.</summary>
    public ScriptedIntentSource(Func<ulong, int, PlayerIntent> script) {
        ArgumentNullException.ThrowIfNull(script);

        m_script = script;
    }

    /// <inheritdoc/>
    public void BeginFrame(ulong firstTick) { }

    /// <inheritdoc/>
    public PlayerIntent[] CollectTick(ulong tick, IReadOnlyList<Guid> players) {
        var intents = new PlayerIntent[players.Count];

        for (var slot = 0; (slot < players.Count); slot++) {
            intents[slot] = m_script(arg1: tick, arg2: slot);
        }

        return intents;
    }
}
