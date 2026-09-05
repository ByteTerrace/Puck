namespace Puck.World.Server;

/// <summary>Identifies one authoritative world instance — an owned world, keyed the same way its own storage
/// address is: the owner's Entra oid plus the world's own <see cref="SafeName"/>.</summary>
public readonly record struct WorldAuthorityIdentity(Guid Owner, SafeName World);
