namespace Puck.World.Server;

/// <summary>Identifies one authoritative world instance — an owned world, keyed the same way its own storage
/// address is: the owner's UUID plus the world's own <see cref="SafeName"/>.</summary>
public readonly record struct WorldAuthorityIdentity(Guid Owner, SafeName World) {
    /// <summary>Gets the key this world's checkpoint and recovery root is held under (see
    /// <see cref="WorldSiloWorldRow.RootKeyOf"/>).</summary>
    public string RootKey => WorldSiloWorldRow.RootKeyOf(owner: Owner, world: World);
}
