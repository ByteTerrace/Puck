using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>How a <see cref="WorldPlacementPortal"/>'s destination instance is minted — mirrors
/// <c>Puck.World.WorldInstanceHost.TransferLifetime</c>'s own vocabulary, but only the two cases a DOCUMENT can
/// author in advance: a portal always creates-or-finds its destination BY DOCUMENT, so it never spells
/// <c>TransferLifetime.Existing</c> (naming an already-running instance is a live fact no author can pin ahead of
/// time). The 4b diegetic trigger maps this one-for-one onto <c>WorldInstanceHost.TransferDestination.Fresh</c>/
/// <c>.Persistent</c> when it actually enqueues the transfer.</summary>
public enum WorldPortalLifetime {
    /// <summary>A BRAND-NEW destination instance, minted fresh on every use — mirrors
    /// <c>WorldInstanceHost.TransferLifetime.Fresh</c>.</summary>
    Fresh,

    /// <summary>A STABLE, named destination instance — reused across travelers, started once and retained — mirrors
    /// <c>WorldInstanceHost.TransferLifetime.Persistent</c>. Requires <see cref="WorldPlacementPortal.Instance"/>.</summary>
    Persistent,
}

/// <summary>Who travels through a <see cref="WorldPlacementPortal"/> — mirrors
/// <c>Puck.World.WorldInstanceHost.TransferScope</c>.</summary>
public enum WorldPortalTravel {
    /// <summary>The traveling seat's WHOLE active local-seat party — mirrors <c>WorldInstanceHost.TransferScope.Party</c>.</summary>
    Party,

    /// <summary>One seat only — mirrors <c>WorldInstanceHost.TransferScope.Body</c>.</summary>
    Body,
}

/// <summary>The document/console token map for <see cref="WorldPortalLifetime"/>/<see cref="WorldPortalTravel"/> —
/// the ONE spelling shared by this document's JSON converters (<see cref="WorldPortalLifetimeJsonConverter"/>/
/// <see cref="WorldPortalTravelJsonConverter"/> in <see cref="WorldDefinitionSerialization"/>) and the SAME lowercase
/// tokens <c>Puck.World.WorldInstanceCommandModule</c>'s <c>world.transfer</c> verb already speaks
/// (<c>fresh &lt;site&gt; &lt;path&gt;</c> / <c>persistent &lt;name&gt; &lt;path&gt;</c> / the bare <c>party</c>
/// token) — mirrors <c>WorldHostTokens</c>'s own role for the host section's backend/surface-format tokens, so a
/// portal-authored document and the console grammar its (chartered, not-yet-built) diegetic trigger will eventually
/// drive never disagree on spelling.</summary>
public static class WorldPortalTokens {
    /// <summary>The document/verb token for <see cref="WorldPortalLifetime.Fresh"/>.</summary>
    public const string LifetimeFresh = "fresh";
    /// <summary>The document/verb token for <see cref="WorldPortalLifetime.Persistent"/>.</summary>
    public const string LifetimePersistent = "persistent";
    /// <summary>The document/verb token for <see cref="WorldPortalTravel.Party"/>.</summary>
    public const string TravelParty = "party";
    /// <summary>The document/verb token for <see cref="WorldPortalTravel.Body"/>.</summary>
    public const string TravelBody = "body";

    /// <summary>Returns the document/verb token for a lifetime.</summary>
    /// <param name="lifetime">The lifetime.</param>
    /// <returns>The lowercase token.</returns>
    public static string LifetimeToken(WorldPortalLifetime lifetime) => lifetime switch {
        WorldPortalLifetime.Persistent => LifetimePersistent,
        _ => LifetimeFresh,
    };

    /// <summary>Parses a lifetime token (case-insensitive), or <see langword="null"/> when the token names none.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed lifetime, or <see langword="null"/>.</returns>
    public static WorldPortalLifetime? ParseLifetime(string? token) => token?.ToLowerInvariant() switch {
        LifetimeFresh => WorldPortalLifetime.Fresh,
        LifetimePersistent => WorldPortalLifetime.Persistent,
        _ => null,
    };

    /// <summary>Returns the document/verb token for a travel scope.</summary>
    /// <param name="travel">The travel scope.</param>
    /// <returns>The lowercase token.</returns>
    public static string TravelToken(WorldPortalTravel travel) => travel switch {
        WorldPortalTravel.Party => TravelParty,
        _ => TravelBody,
    };

    /// <summary>Parses a travel token (case-insensitive), or <see langword="null"/> when the token names none.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed travel scope, or <see langword="null"/>.</returns>
    public static WorldPortalTravel? ParseTravel(string? token) => token?.ToLowerInvariant() switch {
        TravelParty => WorldPortalTravel.Party,
        TravelBody => WorldPortalTravel.Body,
        _ => null,
    };
}

/// <summary>
/// A <see cref="WorldPlacementFace"/>'s PORTAL facet — the authored DECISION that a face is a door: which world (a
/// <see cref="WorldReference"/> row) it leads to, and under what lifetime/travel. Absent (the default) means the
/// face is not a door — nothing here fires anything by itself; turning the decision into a diegetic step-into
/// trigger is a LATER step, never authored here. EXTENSIBLE deliberately (an OPTIONAL-member record, the SAME
/// widen-without-moving-existing-members shape <see cref="WorldWaterSection"/>'s own remarks describe): a future
/// FACT-GATE field (an authored predicate a traveler must satisfy to pass) adds cleanly as a new trailing member.
/// </summary>
/// <param name="Destination">The <see cref="WorldReference.Name"/> of the <c>references</c> row naming the
/// destination world. Must resolve to an existing row — an undeclared name refuses by name (see
/// <see cref="WorldDefinitionValidator"/>). No boot-time file-existence check on the referenced document itself,
/// exactly like <see cref="WorldReference"/> — resolving the DOCUMENT is a future consumer's job; resolving the
/// NAME, against this document's own <c>references</c> section, is this facet's own.</param>
/// <param name="Lifetime">How the destination instance is minted (see <see cref="WorldPortalLifetime"/>).</param>
/// <param name="Instance">The persistent instance's stable name. REQUIRED when <paramref name="Lifetime"/> is
/// <see cref="WorldPortalLifetime.Persistent"/> (refused absent — see <see cref="WorldDefinitionValidator"/>);
/// ignored (authoring one is harmless, but it is never read) when <see cref="WorldPortalLifetime.Fresh"/>. Omitted
/// from the wire when null.</param>
/// <param name="Travel">Who travels through (see <see cref="WorldPortalTravel"/>). Absent resolves against the
/// world's own <c>portals.portalDefaults.travel</c> (see <see cref="WorldPortalsSection"/>), or
/// <see cref="WorldPortalTravel.Body"/> when the world declares no <c>portals</c> section at all — see
/// <see cref="WorldDefinitionValidator"/>'s resolution order, echoed by <c>world.portals</c>. Omitted from the wire
/// when null.</param>
public sealed record WorldPlacementPortal(
    string Destination,
    WorldPortalLifetime Lifetime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSafeName? Instance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPortalTravel? Travel = null
);

/// <summary>The world-scope default a portal facet's absent <see cref="WorldPlacementPortal.Travel"/> resolves to —
/// the <c>portals</c> section's one authored fact today (see <see cref="WorldPortalsSection"/>).</summary>
/// <param name="Travel">The default travel scope (see <see cref="WorldPortalTravel"/>).</param>
public sealed record WorldPortalDefaults(WorldPortalTravel Travel);

/// <summary>
/// The <c>portals</c> section — the world-scope defaults a <see cref="WorldPlacementPortal"/> facet resolves
/// against when it does not author its own fact. Optional, for the SAME reason <see cref="WorldWaterSection"/> and
/// <see cref="WorldReference"/>'s own <c>references</c> section are: a world with no portals authors neither this
/// section nor any portal facet, and a required section would refuse every existing document at boot for declaring
/// nothing. Slotted immediately after <c>references</c> in <see cref="WorldDefinition"/>'s declaration order — the
/// two sections are the world-topology cluster a portal composes from (WHICH world, and how travel through it
/// defaults).
/// </summary>
/// <param name="PortalDefaults">The travel default every portal facet in this document falls back to when it
/// authors no <see cref="WorldPlacementPortal.Travel"/> of its own.</param>
public sealed record WorldPortalsSection(WorldPortalDefaults PortalDefaults);
