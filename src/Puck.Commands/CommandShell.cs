using Puck.Platform;

namespace Puck.Commands;

/// <summary>
/// The per-frame input/command pump. It owns no input routing — that is the registry's job (sources in,
/// handlers out). Each frame it clears the previous frame's transient values, adapts raw platform packets
/// onto the keyboard source, and collects this frame's commands from every source (keyboard and stdin
/// alike). Only the platform-packet-to-input adapter is application-specific; it is supplied at
/// construction, so the shell itself stays model-agnostic.
/// </summary>
public sealed class CommandShell {
    private readonly Func<InputPacket, InputSignal?> m_inputAdapter;
    private readonly BindingCommandSource m_keyboardSource;
    private readonly CommandRegistry m_registry;

    /// <summary>Initializes a new instance of the <see cref="CommandShell"/> class.</summary>
    /// <param name="registry">The registry that collects each frame's commands.</param>
    /// <param name="keyboardSource">The binding source raw input packets are enqueued onto.</param>
    /// <param name="standardInputSource">The text source draining piped or typed command lines.</param>
    /// <param name="inputAdapter">The application's adapter from a platform packet to an input signal.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CommandShell(
        CommandRegistry registry,
        BindingCommandSource keyboardSource,
        TextCommandSource standardInputSource,
        Func<InputPacket, InputSignal?> inputAdapter
    ) {
        ArgumentNullException.ThrowIfNull(inputAdapter);
        ArgumentNullException.ThrowIfNull(keyboardSource);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(standardInputSource);

        m_inputAdapter = inputAdapter;
        m_keyboardSource = keyboardSource;
        m_registry = registry;

        // Keyboard first, then stdin: both are pulled every frame, the latter draining any piped or typed
        // command lines through the registry's text path.
        m_registry.AddSource(source: keyboardSource);
        m_registry.AddSource(source: standardInputSource);
    }

    /// <summary>Clears the previous frame's transient command values. Call before enqueuing input.</summary>
    public void BeginFrame() {
        m_registry.BeginFrame();
    }
    /// <summary>Adapts a raw platform packet to an input signal and enqueues it on the keyboard source.</summary>
    /// <param name="packet">The platform packet to adapt and enqueue.</param>
    public void Enqueue(InputPacket packet) {
        if (m_inputAdapter(arg: packet) is { } input) {
            m_keyboardSource.Enqueue(input: input);
        }
    }
    /// <summary>Collects this frame's commands from every source.</summary>
    public void Collect() {
        m_registry.Collect();
    }
}
