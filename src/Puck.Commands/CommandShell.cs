namespace Puck.Commands;

/// <summary>
/// The per-frame text pump. Each frame it drains the queued command lines a producer (a stdin reader, a script)
/// enqueued on the <see cref="TextCommandSource"/> and submits them through the registry's text path.
/// </summary>
/// <remarks>
/// It pumps text and nothing else, deliberately. Physical input has one capture point — <see cref="InputRouter"/>'s
/// per-tick mixer — because that is where a signal becomes deterministic snapshot state carrying a stamped
/// <see cref="CommandPrincipal"/>. A composition root without an <see cref="InputRouter"/> therefore has no way to
/// dispatch bound input at all — that shape is inexpressible by design, and a root that wants controls registers a
/// router.
/// </remarks>
public sealed class CommandShell {
    private readonly TextCommandSource m_textSource;

    /// <summary>Initializes a new instance of the <see cref="CommandShell"/> class.</summary>
    /// <param name="textSource">The text source draining piped or typed command lines.</param>
    /// <exception cref="ArgumentNullException"><paramref name="textSource"/> is <see langword="null"/>.</exception>
    public CommandShell(TextCommandSource textSource) {
        ArgumentNullException.ThrowIfNull(textSource);

        m_textSource = textSource;
    }

    /// <summary>Submits every command line queued since the last call.</summary>
    public void Collect() {
        m_textSource.Collect();
    }
}
