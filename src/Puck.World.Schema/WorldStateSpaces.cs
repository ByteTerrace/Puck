namespace Puck.World;

/// <summary>Finds declared vector embedding spaces by name from a world definition or state section.</summary>
public static class WorldStateSpaces {
    /// <summary>Finds a vector space by name in a list of spaces.</summary>
    /// <param name="spaces">The declared spaces.</param>
    /// <param name="name">The space name.</param>
    /// <returns>The resolved <see cref="StateSpace"/>, or <see langword="null"/> if not found.</returns>
    public static StateSpace? Find(IReadOnlyList<StateSpace>? spaces, string name) {
        if (spaces is null) {
            return null;
        }

        for (var index = 0; index < spaces.Count; index++) {
            var space = spaces[index];
            if (space is not null && string.Equals(space.Name.Value, name, StringComparison.Ordinal)) {
                return space;
            }
        }

        return null;
    }

    /// <summary>Finds a vector space by name in a state section.</summary>
    /// <param name="state">The world state section.</param>
    /// <param name="name">The space name.</param>
    /// <returns>The resolved <see cref="StateSpace"/>, or <see langword="null"/> if not found.</returns>
    public static StateSpace? Find(WorldStateSection? state, string name) =>
        Find(spaces: state?.Spaces, name: name);

    /// <summary>Finds a vector space by name in a world definition.</summary>
    /// <param name="definition">The world definition.</param>
    /// <param name="name">The space name.</param>
    /// <returns>The resolved <see cref="StateSpace"/>, or <see langword="null"/> if not found.</returns>
    public static StateSpace? Find(WorldDefinition definition, string name) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        return Find(state: definition.StateRaw, name: name);
    }

    /// <summary>Resolves the space for a state row from declared spaces, supporting single-space default resolution.</summary>
    /// <param name="spaces">The declared spaces.</param>
    /// <param name="row">The state row.</param>
    /// <param name="space">The resolved space, on success.</param>
    /// <param name="reason">The failure reason, on failure.</param>
    /// <returns><see langword="true"/> when the space resolves; otherwise <see langword="false"/>.</returns>
    public static bool TryResolveSpace(IReadOnlyList<StateSpace>? spaces, StateRow row, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StateSpace? space, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: row);

        space = null;
        reason = string.Empty;

        if (row.Space is not null) {
            if (Find(spaces: spaces, name: row.Space) is { } found) {
                space = found;
                return true;
            }

            reason = $"state row '{row.Name}' declares space '{row.Space}' which is not a declared space";
            return false;
        }

        if (spaces is { Count: 1 }) {
            space = spaces[0];
            return true;
        }

        if (spaces is null or { Count: 0 }) {
            reason = $"state row '{row.Name}' is kind 'vector' but no spaces are declared";
            return false;
        }

        reason = $"state row '{row.Name}' is kind 'vector' without a declared space and multiple spaces are declared";
        return false;
    }
}
