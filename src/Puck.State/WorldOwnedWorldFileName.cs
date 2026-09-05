namespace Puck.State;

/// <summary>
/// The one id↔file-name mapping every owned world is stored under — locally as <c>&lt;name&gt;.world.json</c> beside its
/// catalog, and in the cloud as the object key derived from the same name. The mapping takes a <see cref="WorldSafeName"/>
/// rather than a raw string, so it escapes nothing: the type it arrives as has already refused every character this
/// mapping would otherwise have had to collapse.
/// </summary>
/// <remarks>The mapping is injective into file-name STRINGS, which is not the same as into storage LOCATIONS: NTFS and
/// default APFS resolve a name case-insensitively, so <c>Amber</c> and <c>amber</c> are distinct
/// <see cref="WorldSafeName"/>s addressing one local file (a cloud object namespace is case-SENSITIVE, so the same
/// pair addresses two blobs there). One id therefore addresses one location only under a case-insensitive uniqueness
/// rule, which is held by the two doors that admit an id: the world document's seed list
/// (<c>WorldDefinitionValidator.ValidatePlayerDefaults</c>) and the catalog directory itself
/// (<c>Server.WorldOwnedWorlds</c>). This type lives beside <see cref="WorldSafeName"/> in the state library, beneath
/// the document project and not beside that catalog, because the character rule has to hold at the earliest door: a
/// world document authoring seed identities is validated long before any catalog exists to refuse them.</remarks>
public static class WorldOwnedWorldFileName {
    /// <summary>The file-name suffix every owned world is stored under.</summary>
    public const string Suffix = ".world.json";

    /// <summary>Returns the file name an owned world persists under, locally and in the cloud key space.</summary>
    /// <param name="id">The owned world id.</param>
    /// <returns>The file name.</returns>
    public static string For(WorldSafeName id) => $"{id.Value}{Suffix}";
}
