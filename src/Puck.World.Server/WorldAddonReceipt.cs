namespace Puck.World.Server;

/// <summary>One mounted guest's recorded-at-mount identity — the smallest honest set of facts a replay must
/// re-establish before it may re-run that guest. The world document's <c>WorldAddonRow</c> carries the pin an
/// author wrote; a receipt carries what the mount actually produced, which is the only form that survives the tree
/// moving under a saved tape (<see cref="WorldReplaySnapshot"/> records the set at record-start and refuses a
/// re-drive whose fresh mount disagrees).</summary>
/// <param name="Name">The descriptor name — the key receipts are compared by.</param>
/// <param name="Hash">The mounted module's canonical <c>sha256-64/{hex}</c> content identity.</param>
/// <param name="Fuel">The per-tick fuel budget the instance runs under.</param>
public readonly record struct WorldAddonReceipt(string Name, string Hash, ulong Fuel);
