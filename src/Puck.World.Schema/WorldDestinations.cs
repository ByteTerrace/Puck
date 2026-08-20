using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>How a <see cref="WorldDestination"/>'s target instance is minted — mirrors
/// <c>Puck.World.WorldInstanceHost.TransferLifetime</c>'s own vocabulary, but only the two cases a document can
/// author in advance: a destination always creates-or-finds its instance by document, so it never spells
/// <c>TransferLifetime.Existing</c> (naming an already-running instance is a live fact no author can pin ahead of
/// time). <c>WorldInstanceHost.TriggerPortal</c> maps this one-for-one onto
/// <c>WorldInstanceHost.TransferDestination.Fresh</c>/<c>.Persistent</c> when it actually enqueues the transfer — an
/// interim mapping (see that method's own remarks) that a transport-neutral local resolver, not yet built,
/// supersedes.</summary>
public enum WorldDestinationDurability {
    /// <summary>A target-issued generation that is not recovered after its lifecycle ends — a brand-new instance
    /// minted fresh on every use. Mirrors <c>WorldInstanceHost.TransferLifetime.Fresh</c>.</summary>
    Ephemeral,

    /// <summary>Durable simulation state that may unload, hydrate, or move between hosts — a stable instance reused
    /// across travelers, started once and retained. Mirrors <c>WorldInstanceHost.TransferLifetime.Persistent</c>.
    /// Interim (see <c>WorldInstanceHost.TriggerPortal</c>'s own remarks): until the resolver lands, a persisted
    /// destination's instance name is its own <see cref="WorldDestination.Name"/> — there is no separate authored
    /// instance name any more.</summary>
    Persisted,
}
/// <summary>Which scoped identity/generation a <see cref="WorldDestination"/> selects (docs/vision.md,
/// "Durability, scope and generation"). Absent on the wire resolves to <see cref="Global"/> — today's behavior,
/// unchanged for every destination row authored before this member existed.</summary>
public enum WorldDestinationScope {
    /// <summary>Resolves to the entering seat's own owned-identity world — the identity is the user. An anonymous
    /// seat (no identity) refuses by name rather than minting one.</summary>
    User,

    /// <summary>Resolves through a <see cref="WorldGroupSelector"/> — <see cref="WorldDestination.Selector"/> is
    /// required exactly when <see cref="WorldDestination.Scope"/> is this value (refused by name otherwise, in
    /// either direction).</summary>
    Group,

    /// <summary>Resolves to the destination's own shared key — every traveler not otherwise scoped lands in the same
    /// generation. The default; scope is not permission, so a global destination can still remain private through the
    /// engine's ordinary target-admission door.</summary>
    Global,
}
/// <summary>Selects which group a <see cref="WorldDestinationScope.Group"/> destination resolves through — the
/// <c>$type</c>-discriminated union docs/vision.md's "Durability, scope and generation" names. Required exactly
/// when <see cref="WorldDestination.Scope"/> is <see cref="WorldDestinationScope.Group"/>; admitted nowhere else. A
/// future selection form widens this union with another <c>$type</c> arm rather than adding parallel optional fields
/// to <see cref="WorldDestination"/> itself (the same closed-union discipline <see cref="WorldCameraSubject"/>/
/// <see cref="WorldAnchor"/> already follow).</summary>
[JsonDerivedType(typeof(WorldGroupSelector.Named), typeDiscriminator: "named")]
[JsonDerivedType(typeof(WorldGroupSelector.Tagged), typeDiscriminator: "tagged")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldGroupSelector {
    private WorldGroupSelector() {
    }

    /// <summary>Assigns the destination to exactly one authored group — every traveler must prove membership in
    /// <paramref name="Group"/> specifically.</summary>
    /// <param name="Group">The <see cref="WorldGroup.Id"/> this destination is bound to. Must resolve to a declared
    /// group row — an undeclared id refuses by name.</param>
    public sealed record Named(string Group) : WorldGroupSelector;
    /// <summary>Selects the traveler's unique verified membership claim carrying <paramref name="Tag"/> — zero
    /// matching memberships and multiple matching memberships are distinct named refusals; the engine never picks
    /// one silently (see <see cref="WorldGroup.Tags"/>).</summary>
    /// <param name="Tag">The tag every candidate membership is matched against. Must be non-empty.</param>
    public sealed record Tagged(string Tag) : WorldGroupSelector;
}
/// <summary>
/// One row of the <c>destinations</c> section — scoped selection layered over exactly one <see cref="WorldReference"/>
/// row (docs/vision.md, "Reference, destination and session are different facts"). A <see cref="WorldReference"/>
/// names a document; a destination decides how an instance of that document is minted and reused. Several
/// destinations may select one reference differently — a fresh group dungeon, a persisted user workshop, and a
/// shared global zone can all point at the same document.
/// </summary>
/// <remarks>Boot-authored document data only, exactly like <see cref="WorldReference"/> and the portal facet it now
/// layers under: no live mutation arm, no <c>Protocol.WorldSection</c> axis, no grant subject. Making a destination
/// live-editable later is a complete mutation-axis addition, not an accidental consequence of introducing the
/// row.</remarks>
/// <param name="Name">The destination's own name — <see cref="WorldSafeName"/>-shaped, unique within the section. A
/// <see cref="WorldPlacementPortal.Destination"/> facet resolves against this name (see
/// <see cref="WorldDefinitionValidator"/>).</param>
/// <param name="Reference">The <see cref="WorldReference.Name"/> of the <c>references</c> row this destination
/// selects. Must resolve to an existing row — an undeclared name refuses by name. Never repeats
/// <see cref="WorldReference.Document"/> itself: several destinations may select one reference differently.</param>
/// <param name="Durability">How this destination's target instance is minted (see
/// <see cref="WorldDestinationDurability"/>).</param>
/// <param name="Scope">Which scoped identity/generation this destination selects (see
/// <see cref="WorldDestinationScope"/>). Absent resolves to <see cref="WorldDestinationScope.Global"/> — today's
/// behavior, unchanged for every row authored before this member existed. Trailing member: the same
/// widen-without-moving-existing-members shape <see cref="WorldWaterSection"/>'s own remarks describe.</param>
/// <param name="Selector">Which group a <see cref="WorldDestinationScope.Group"/> row resolves through (see
/// <see cref="WorldGroupSelector"/>). Required exactly when <paramref name="Scope"/> is
/// <see cref="WorldDestinationScope.Group"/> — a selector on any other scope, or a group scope with none, refuses by
/// name (see <see cref="WorldDefinitionValidator"/>).</param>
public sealed record WorldDestination(
    WorldSafeName Name,
    string Reference,
    WorldDestinationDurability Durability,
    WorldDestinationScope Scope = WorldDestinationScope.Global,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGroupSelector? Selector = null
);
/// <summary>The document/console token map for <see cref="WorldDestinationDurability"/>/<see cref="WorldPortalTravel"/>/
/// <see cref="WorldPortalArrival"/>
/// — the one spelling shared by this document's JSON converters
/// (<see cref="WorldDestinationDurabilityJsonConverter"/>/<see cref="WorldPortalTravelJsonConverter"/>/
/// <see cref="WorldPortalArrivalJsonConverter"/> in
/// <see cref="WorldDefinitionSerialization"/>) and the same lowercase tokens
/// <c>Puck.World.WorldInstanceCommandModule</c>'s <c>world.transfer</c> verb already speaks (<c>ephemeral &lt;site&gt;
/// &lt;path&gt;</c> / <c>persisted &lt;name&gt; &lt;path&gt;</c> / the bare <c>party</c> token) — mirrors
/// <c>WorldHostTokens</c>'s own role for the host section's backend/surface-format tokens, so an authored document
/// and the console grammar its diegetic trigger (<c>WorldInstanceHost.TriggerPortal</c>, already built) drives never
/// disagree on spelling. Supersedes the old <c>WorldPortalTokens</c>, which spelled these <c>fresh</c>/
/// <c>persistent</c> — retired in the same change that moved lifetime/instance off the portal facet and onto this
/// section (supergreen; no compatibility window).</summary>
public static class WorldDestinationTokens {
    /// <summary>The document token for <see cref="WorldPortalArrival.Mapped"/>.</summary>
    public const string ArrivalMapped = "mapped";
    /// <summary>The document token for <see cref="WorldPortalArrival.Spawn"/>.</summary>
    public const string ArrivalSpawn = "spawn";
    /// <summary>The document/verb token for <see cref="WorldDestinationDurability.Ephemeral"/>.</summary>
    public const string DurabilityEphemeral = "ephemeral";
    /// <summary>The document/verb token for <see cref="WorldDestinationDurability.Persisted"/>.</summary>
    public const string DurabilityPersisted = "persisted";
    /// <summary>The document/verb token for <see cref="WorldDestinationScope.Global"/>.</summary>
    public const string ScopeGlobal = "global";
    /// <summary>The document/verb token for <see cref="WorldDestinationScope.Group"/>.</summary>
    public const string ScopeGroup = "group";
    /// <summary>The document/verb token for <see cref="WorldDestinationScope.User"/>.</summary>
    public const string ScopeUser = "user";
    /// <summary>The document/verb token for <see cref="WorldPortalTravel.Body"/>.</summary>
    public const string TravelBody = "body";
    /// <summary>The document/verb token for <see cref="WorldPortalTravel.Party"/>.</summary>
    public const string TravelParty = "party";

    /// <summary>Returns the document token for an arrival mode.</summary>
    /// <param name="arrival">The arrival mode.</param>
    /// <returns>The lowercase token.</returns>
    public static string ArrivalToken(WorldPortalArrival arrival) => arrival switch {
        WorldPortalArrival.Mapped => ArrivalMapped,
        _ => ArrivalSpawn,
    };
    /// <summary>Returns the document/verb token for a durability.</summary>
    /// <param name="durability">The durability.</param>
    /// <returns>The lowercase token.</returns>
    public static string DurabilityToken(WorldDestinationDurability durability) => durability switch {
        WorldDestinationDurability.Persisted => DurabilityPersisted,
        _ => DurabilityEphemeral,
    };
    /// <summary>Parses an arrival token (case-insensitive), or <see langword="null"/> when the token names none.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed arrival mode, or <see langword="null"/>.</returns>
    public static WorldPortalArrival? ParseArrival(string? token) => token?.ToLowerInvariant() switch {
        ArrivalSpawn => WorldPortalArrival.Spawn,
        ArrivalMapped => WorldPortalArrival.Mapped,
        _ => null,
    };
    /// <summary>Parses a durability token (case-insensitive), or <see langword="null"/> when the token names none.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed durability, or <see langword="null"/>.</returns>
    public static WorldDestinationDurability? ParseDurability(string? token) => token?.ToLowerInvariant() switch {
        DurabilityEphemeral => WorldDestinationDurability.Ephemeral,
        DurabilityPersisted => WorldDestinationDurability.Persisted,
        _ => null,
    };
    /// <summary>Parses a scope token (case-insensitive), or <see langword="null"/> when the token names none.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed scope, or <see langword="null"/>.</returns>
    public static WorldDestinationScope? ParseScope(string? token) => token?.ToLowerInvariant() switch {
        ScopeUser => WorldDestinationScope.User,
        ScopeGroup => WorldDestinationScope.Group,
        ScopeGlobal => WorldDestinationScope.Global,
        _ => null,
    };
    /// <summary>Parses a travel token (case-insensitive), or <see langword="null"/> when the token names none.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed travel scope, or <see langword="null"/>.</returns>
    public static WorldPortalTravel? ParseTravel(string? token) => token?.ToLowerInvariant() switch {
        TravelParty => WorldPortalTravel.Party,
        TravelBody => WorldPortalTravel.Body,
        _ => null,
    };
    /// <summary>Returns the document/verb token for a scope.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The lowercase token.</returns>
    public static string ScopeToken(WorldDestinationScope scope) => scope switch {
        WorldDestinationScope.User => ScopeUser,
        WorldDestinationScope.Group => ScopeGroup,
        _ => ScopeGlobal,
    };
    /// <summary>Returns the document/verb token for a travel scope.</summary>
    /// <param name="travel">The travel scope.</param>
    /// <returns>The lowercase token.</returns>
    public static string TravelToken(WorldPortalTravel travel) => travel switch {
        WorldPortalTravel.Party => TravelParty,
        _ => TravelBody,
    };
}
