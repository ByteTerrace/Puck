namespace Puck.World.Client;

/// <summary>The state binding a look's render-lane state operand reads through: a row read binds its row and key as
/// written, and a <c>bodyState($body, slot)</c> read binds the slot's <see cref="WorldBodyStateReads.RowPrefix"/> row
/// keyed by the body (<see cref="WorldBodyStateReads.TryBinding"/>). The presentation manifest and the lane evaluator
/// both bind through it, so the slot the manifest registers is the slot the lane reads.</summary>
public static class WorldLookLaneReads {
    /// <summary>Returns the binding one lane state operand reads.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The binding, reading the eased value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is <see langword="null"/>.</exception>
    public static StateBinding Binding(InstructionPayload.State operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return (WorldBodyStateReads.TryBinding(
            binding: out var bodyState,
            key: operand.Key,
            name: operand.Name
        )
            ? bodyState
            : new StateBinding(
                Key: operand.Key?.Spelling,
                Row: operand.Name.Spelling,
                Target: false
            )
        );
    }
}
