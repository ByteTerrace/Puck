namespace Puck.Commands;

/// <summary>
/// The per-frame input/command pump. It owns no input routing — that is the registry's job (sources in,
/// handlers out). Each frame it clears the previous frame's transient values, enqueues the platform's input
/// signals onto the keyboard source, and collects this frame's commands from every source (keyboard and
/// stdin alike). The platform emits <see cref="InputSignal"/>s directly, so the shell needs no adapter.
/// </summary>
public sealed class CommandShell {
    private readonly BindingCommandSource m_keyboardSource;
    private readonly CommandRegistry m_registry;

    /// <summary>Initializes a new instance of the <see cref="CommandShell"/> class.</summary>
    /// <param name="registry">The registry that collects each frame's commands.</param>
    /// <param name="bindingSource">The binding source raw input signals are enqueued onto.</param>
    /// <param name="textSource">The text source draining piped or typed command lines.</param>
    /// <param name="additionalSources">
    /// Extra sources to register, such as a gamepad source — each owns its own production (and may run its own
    /// device threads); the registry simply pulls them every frame like the keyboard and text sources.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/>, <paramref name="bindingSource"/>, or <paramref name="textSource"/> is <see langword="null"/>.</exception>
    public CommandShell(
        CommandRegistry registry,
        BindingCommandSource bindingSource,
        TextCommandSource textSource,
        IEnumerable<ICommandSource>? additionalSources = null
    ) {
        ArgumentNullException.ThrowIfNull(bindingSource);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(textSource);

        m_keyboardSource = bindingSource;
        m_registry = registry;

        m_registry.AddSource(source: bindingSource);
        m_registry.AddSource(source: textSource);

        if (additionalSources is not null) {
            foreach (var source in additionalSources) {
                m_registry.AddSource(source: source);
            }
        }
    }

    /// <summary>Clears the previous frame's transient command values. Call before enqueuing input.</summary>
    public void BeginFrame() {
        m_registry.BeginFrame();
    }
    /// <summary>Collects this frame's commands from every source.</summary>
    public void Collect() {
        m_registry.Collect();
    }
    /// <summary>Clears all held digital values, so nothing stays asserted (call on focus loss).</summary>
    public void ReleaseHeld() {
        m_registry.ReleaseHeld();
    }
    /// <summary>Enqueues a platform input signal on the keyboard source.</summary>
    /// <param name="signal">The input signal to enqueue.</param>
    public void Enqueue(InputSignal signal) {
        m_keyboardSource.Enqueue(input: signal);
    }
}
