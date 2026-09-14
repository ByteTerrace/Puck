namespace Puck.World;

/// <summary>Finds declared vector embedding spaces by name from a world definition or state section.</summary>
public static class WorldStateSpaces {
    /// <summary>Finds a vector space by name in a state section.</summary>
    /// <param name="state">The world state section.</param>
    /// <param name="name">The space name.</param>
    /// <returns>The resolved <see cref="StateSpace"/>, or <see langword="null"/> if not found.</returns>
    public static StateSpace? Find(WorldStateSection? state, string name) {
        if (state?.Spaces is not { } spaces) {
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

    /// <summary>Finds a vector space by name in a world definition.</summary>
    /// <param name="definition">The world definition.</param>
    /// <param name="name">The space name.</param>
    /// <returns>The resolved <see cref="StateSpace"/>, or <see langword="null"/> if not found.</returns>
    public static StateSpace? Find(WorldDefinition definition, string name) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        return Find(state: definition.StateRaw, name: name);
    }
}
