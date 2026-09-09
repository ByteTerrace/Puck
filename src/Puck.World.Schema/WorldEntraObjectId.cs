namespace Puck.World;

/// <summary>
/// The one Entra object-id shape check for an authored oid field — an oid is a Guid string. Both
/// <see cref="WorldMetadataAuthor.Oid"/> (refused by name at document validation) and
/// <c>Puck.World.ExplicitOverridePlayerStorageIdentityResolver</c> (the <c>storage.userId</c> identity path,
/// which declines at runtime rather than refusing at validation) route their shape test through this one
/// implementation.
/// </summary>
public static class WorldEntraObjectId {
    /// <summary>Whether <paramref name="value"/> is a well-formed, non-empty Entra object id.</summary>
    /// <param name="value">The candidate oid string.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> parses as a non-empty <see cref="Guid"/>.</returns>
    public static bool IsValid(string value) => (Guid.TryParse(
        input: value,
        result: out var parsed
    ) && (parsed != Guid.Empty));
}
