namespace Puck.Commands;

/// <summary>
/// An <see cref="ICommandSource"/> that rewrites enqueued <see cref="InputSignal"/>s into command
/// activations using a binding table.
/// </summary>
/// <remarks>
/// Producers feed raw input with <see cref="Enqueue"/>; each frame, <see cref="Collect"/> looks up the
/// bindings for every input's <see cref="InputSignal.Source"/> and pushes one <see cref="CommandSignal"/>
/// per binding. A single input may bind to several commands across different maps; the registry's map
/// gating keeps whichever is active, so this source remains modality-agnostic.
/// </remarks>
public sealed class BindingCommandSource : ICommandSource {
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> m_bindings;
    private readonly List<InputSignal> m_pending = [];

    /// <summary>Initializes a new instance of the <see cref="BindingCommandSource"/> class.</summary>
    /// <param name="bindings">The table mapping each input source id to the commands it activates.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    public BindingCommandSource(IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> bindings) {
        ArgumentNullException.ThrowIfNull(bindings);

        m_bindings = bindings;
    }

    /// <summary>Queues a raw input activation to be bound on the next <see cref="Collect"/>.</summary>
    /// <param name="input">The input activation to queue.</param>
    public void Enqueue(InputSignal input) {
        m_pending.Add(item: input);
    }

    /// <summary>Binds every input enqueued since the last call and pushes the resulting signals.</summary>
    /// <param name="sink">The sink that receives the bound command activations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public void Collect(ICommandSink sink) {
        ArgumentNullException.ThrowIfNull(sink);

        foreach (var input in m_pending) {
            if (!m_bindings.TryGetValue(
                key: input.Source,
                value: out var bindings
            )) {
                continue;
            }

            foreach (var binding in bindings) {
                sink.Push(signal: new CommandSignal(
                    Name: binding.Command,
                    Phase: input.Phase,
                    Text: input.Text,
                    Value: (binding.Value ?? input.Value)
                ));
            }
        }

        m_pending.Clear();
    }
}
