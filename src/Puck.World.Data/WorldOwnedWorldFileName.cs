namespace Puck.World;

/// <summary>
/// The one id↔file-name mapping every owned world is stored under — locally as <c>&lt;name&gt;.world.json</c> beside its
/// catalog, and in the cloud as the object key derived from the same name. The mapping takes a <see cref="WorldSafeName"/>
/// rather than a raw string, so it is INJECTIVE by construction: two distinct ids can never collapse onto the same file
/// name, because the type they arrive as already refuses every character this mapping would otherwise have needed to
/// escape.
/// </summary>
/// <remarks>This lives in the DOCUMENT project, not beside the catalog that saves through it, because the rule has to
/// hold at the earliest door: a world document authoring seed identities is validated long before any catalog exists
/// to refuse them, and a rule enforced only at the catalog is a rule an authored document walks straight past.</remarks>
public static class WorldOwnedWorldFileName {
    /// <summary>The file-name suffix every owned world is stored under.</summary>
    public const string Suffix = ".world.json";

    /// <summary>Returns the file name an owned world persists under, locally and in the cloud key space.</summary>
    /// <param name="id">The owned world id.</param>
    /// <returns>The file name.</returns>
    public static string For(WorldSafeName id) => $"{id.Value}{Suffix}";
}
