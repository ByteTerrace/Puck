namespace Puck.Commands;

/// <summary>
/// Builds the runtime binding table — the <c>source → commands</c> dictionary an <see cref="IInputBindings"/>
/// consumes — from a flat list of data-driven <see cref="InputBindingDefinition"/>s. This is the bridge from a
/// loaded binding profile (default or per-player) to the in-memory tables.
/// </summary>
public static class InputBindingTable {
    /// <summary>Groups definitions by source into a binding table. Multiple definitions for one source are kept in order.</summary>
    /// <param name="definitions">The data-driven binding definitions to build from.</param>
    /// <returns>A table mapping each source id to its ordered list of bindings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definitions"/> is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> Build(IEnumerable<InputBindingDefinition> definitions) {
        ArgumentNullException.ThrowIfNull(definitions);

        var grouped = new Dictionary<string, List<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions) {
            if (!grouped.TryGetValue(
                key: definition.Source,
                value: out var list
            )) {
                list = [];
                grouped[definition.Source] = list;
            }

            list.Add(item: new CommandBinding(
                ActivateOn: definition.ActivateOn,
                Command: definition.Command,
                RequiredModifiers: definition.RequiredModifiers
            ));
        }

        var table = new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var (source, list) in grouped) {
            table[source] = list;
        }

        return table;
    }
}
