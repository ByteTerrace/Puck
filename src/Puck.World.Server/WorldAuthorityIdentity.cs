namespace Puck.World.Server;

/// <summary>Identifies one authoritative world instance — an owned world, keyed the same way its own storage
/// address is: the owner's UUID plus the world's own <see cref="SafeName"/>.</summary>
public readonly record struct WorldAuthorityIdentity(Guid Owner, SafeName World) {
    /// <summary>Gets the key this world's checkpoint and recovery root is held under (see
    /// <see cref="WorldSiloWorldRow.RootKeyOf"/>).</summary>
    public string RootKey => WorldSiloWorldRow.RootKeyOf(owner: Owner, world: World);

    /// <summary>Parses a root key (<see cref="RootKey"/>, <c>owner/world</c>), as a release manifest names its worlds.</summary>
    /// <param name="rootKey">The root key.</param>
    /// <returns>The identity it names.</returns>
    /// <exception cref="InvalidDataException"><paramref name="rootKey"/> is not a non-empty owner UUID and a safe world
    /// name separated by one slash.</exception>
    public static WorldAuthorityIdentity ParseRootKey(string rootKey) {
        var pieces = rootKey.Split('/');

        if (
            (pieces.Length != 2) ||
            !Guid.TryParseExact(input: pieces[0], format: "D", result: out var owner) ||
            (owner == Guid.Empty) ||
            !SafeName.TryParse(candidate: pieces[1], name: out var world, reason: out _)
        ) { throw new InvalidDataException(message: $"'{rootKey}' is not a stable world identity"); }
        return new(Owner: owner, World: world);
    }
}
