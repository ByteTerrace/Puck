using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Puck.World.Protocol;

/// <summary>The local roster's published catalog and the unambiguous controller preferences for this host.</summary>
public sealed record WorldRosterProfiles(
    ImmutableArray<WorldIdentityProjection> Catalog,
    FrozenDictionary<Guid, string> PreferredIds
);
