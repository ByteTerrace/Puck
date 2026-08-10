using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The trade shapes a <see cref="WorldMarketListing"/> may declare — authored rule shapes over the
/// total-ordered bid journal, never a free-form auction engine.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldMarketFormat>))]
public enum WorldMarketFormat : byte {
    /// <summary>An ascending-bid auction: each accepted bid must strictly exceed the current one (or the listing's
    /// <see cref="WorldMarketListing.StartPrice"/> if none stands yet). May optionally also carry a
    /// <see cref="WorldMarketListing.BuyoutPrice"/> for an instant win.</summary>
    English,

    /// <summary>A fixed-price sale: settles immediately to whichever <c>market.buyout</c> submission reaches the
    /// listing first, at its declared <see cref="WorldMarketListing.BuyoutPrice"/>. Takes no incremental bids.</summary>
    Buyout,
}

/// <summary>The lifecycle a <see cref="WorldMarketListing"/> passes through — set once, by exactly one of
/// <see cref="WorldMutation.BuyoutMarketListing"/>, <see cref="WorldMutation.CancelMarketListing"/>, or
/// <see cref="WorldMutation.SettleMarketListing"/>, and never reverted.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldMarketListingStatus>))]
public enum WorldMarketListingStatus : byte {
    /// <summary>Open — accepting bids (English) or a buyout, and reachable by the deadline sweep.</summary>
    Active,

    /// <summary>Resolved to a winner — the item and payment have both moved.</summary>
    Settled,

    /// <summary>Withdrawn by its seller before a winner resolved — the escrowed item (and any escrowed bid) was
    /// returned in the same mutation that set this status.</summary>
    Cancelled,

    /// <summary>Reached its deadline with no bid ever placed — the escrowed item was returned in the same mutation
    /// that set this status.</summary>
    Expired,
}

/// <summary>One per-tier admission rule a <see cref="WorldMarketSection"/> may declare. Validated and round-tripped
/// today; not yet consulted by any listing/bid/buyout compose arm — LOCAL enforcement is admit-everyone (the trivial
/// self-mint posture the fact vocabulary's <see cref="WorldStateCell.Provenance"/> also takes), and a federated
/// authority is expected to consult this row before it enforces attestation.</summary>
/// <param name="Name">The tier's stable name, unique within the section.</param>
/// <param name="RequireAttestation">Whether a participant must carry a ring attestation to trade under this tier —
/// authored now, inert until federation reads it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct WorldMarketAdmissionTier(
    string Name,
    bool RequireAttestation = false
);

/// <summary>One listing on the market — an item escrowed out of its seller's spendable set the moment this row is
/// created, resolved exactly once by a buyout, a cancel, or the deadline sweep. <see cref="ItemRow"/>/
/// <see cref="CurrencyRow"/> each name a keyed <c>state</c> row (see <see cref="WorldStateCell"/>'s remarks) whose
/// cells are addressed by a holder's 0-based <see cref="WorldPrincipal.Index"/> — the item/currency fact
/// vocabulary this listing moves quantity through, never a second inventory system.</summary>
/// <param name="Id">The listing's engine-minted, monotonically increasing id (see
/// <see cref="WorldMarketSection.NextListingId"/>).</param>
/// <param name="Seller">The seller — always a <see cref="PrincipalKind.Seat"/> or <see cref="PrincipalKind.Peer"/>
/// principal (a real player, never the console/world/an addon/a group).</param>
/// <param name="ItemRow">The keyed state row carrying the traded item's per-holder quantity.</param>
/// <param name="Quantity">How much of <see cref="ItemRow"/> this listing escrows — decremented from the seller's own
/// cell at creation, credited to the winner's cell at settlement, or returned to the seller's cell on cancel/expiry.</param>
/// <param name="CurrencyRow">The keyed state row carrying the price currency's per-holder balance.</param>
/// <param name="Format">Which trade shape this listing runs.</param>
/// <param name="StartPrice">The minimum opening bid (<see cref="WorldMarketFormat.English"/>); unused by, and must
/// be zero for, <see cref="WorldMarketFormat.Buyout"/>.</param>
/// <param name="DeadlineTick">The engine tick at or after which the deadline sweep (<c>Server.WorldServer</c>'s
/// per-tick market pass, the same shape as its own <c>ReclaimExpiredEscrows</c>) settles or expires this listing —
/// derived once, at creation, from the authored duration seconds through
/// <see cref="WorldSimulationTickConversion.DurationTicks(float,uint)"/>; never re-derived.</param>
/// <param name="BuyoutPrice">The instant-win price, or <see langword="null"/> for an English listing carrying none.
/// Required for <see cref="WorldMarketFormat.Buyout"/>.</param>
/// <param name="Status">The listing's current lifecycle state.</param>
/// <param name="CurrentBid">The highest accepted bid so far (<see cref="WorldMarketFormat.English"/> only); zero
/// while unbid.</param>
/// <param name="CurrentBidder">Who placed <see cref="CurrentBid"/>, or <see langword="null"/> while unbid.</param>
/// <param name="ResolvedTick">The tick <see cref="Status"/> left <see cref="WorldMarketListingStatus.Active"/> at, or
/// <see langword="null"/> while still active — the retention sweep's own age basis (<c>Server.WorldServer</c>'s
/// <c>PruneMarketListings</c> compose arm), never re-derived from <see cref="DeadlineTick"/> (a cancel or buyout
/// resolves long before its deadline, so the deadline cannot stand in for "when this row actually became terminal").</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldMarketListing(
    long Id,
    WorldPrincipal Seller,
    WorldCellName ItemRow,
    long Quantity,
    WorldCellName CurrencyRow,
    WorldMarketFormat Format,
    long StartPrice,
    long DeadlineTick,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BuyoutPrice = null,
    WorldMarketListingStatus Status = WorldMarketListingStatus.Active,
    long CurrentBid = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPrincipal? CurrentBidder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ResolvedTick = null
);

/// <summary>The <c>market</c> document section — a single-authority local auction house. OPTIONAL, like
/// <c>groups</c>/<c>water</c>: a document declaring none carries <see langword="null"/> here, which is exactly
/// today's no-market behavior (every <c>market.*</c> verb refuses by name against a world that never authored this
/// section). Only <c>Puck.World</c>'s <c>play</c> world authors one among the shipped worlds.</summary>
/// <param name="Formats">Which <see cref="WorldMarketFormat"/> values a listing may declare, or <see langword="null"/>
/// to permit both.</param>
/// <param name="FeeBasisPoints">The house fee, in basis points of a settled sale's price
/// (0..<see cref="WorldMarketCapacity.MaxFeeBasisPoints"/>), credited to <see cref="FeeReserve"/> at settlement —
/// never destroyed, so escrow conservation holds across the fee too.</param>
/// <param name="MinDurationSeconds">The least authored listing duration this market admits.</param>
/// <param name="MaxDurationSeconds">The greatest authored listing duration this market admits.</param>
/// <param name="AdmissionTiers">The declared per-tier admission rules (see <see cref="WorldMarketAdmissionTier"/>) —
/// validated and round-tripped, locally inert.</param>
/// <param name="Listings">The live listing ledger — engine- and player-mutated, never re-seeded from the document on
/// boot the way <see cref="Formats"/>/<see cref="AdmissionTiers"/> are (matches <c>groups</c>' authored-vs-runtime
/// split for its own <see cref="WorldOwnership"/> rows).</param>
/// <param name="NextListingId">The next <see cref="WorldMarketListing.Id"/> a <see cref="WorldMutation.CreateMarketListing"/>
/// mints — engine bookkeeping, monotonically increasing, never reused.</param>
/// <param name="FeeReserve">The house's accumulated fee take — engine bookkeeping; the fee sink escrow conservation
/// balances against.</param>
/// <param name="RetentionSeconds">How long a terminal row (<see cref="WorldMarketListingStatus.Settled"/>/
/// <see cref="WorldMarketListingStatus.Cancelled"/>/<see cref="WorldMarketListingStatus.Expired"/>) stands, past its
/// own <see cref="WorldMarketListing.ResolvedTick"/>, before the engine's own per-tick retention sweep
/// (<c>Server.WorldServer</c>'s <c>PruneMarketListings</c> compose arm) archives it — the bound half of
/// <see cref="WorldMarketCapacity.MaxListings"/>: an active row is never eligible however old, so a market that never resolves anything
/// still fills at exactly <see cref="WorldMarketCapacity.MaxListings"/> live listings. Converted to ticks the same way a listing's own
/// <see cref="WorldMarketSection.MinDurationSeconds"/>/<see cref="WorldMarketSection.MaxDurationSeconds"/> become its
/// <see cref="WorldMarketListing.DeadlineTick"/> (<see cref="WorldSimulationTickConversion.DurationTicks(float,uint)"/>).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldMarketSection(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMarketFormat>? Formats = null,
    int FeeBasisPoints = 0,
    float MinDurationSeconds = 60f,
    float MaxDurationSeconds = 86400f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMarketAdmissionTier>? AdmissionTiers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMarketListing>? Listings = null,
    long NextListingId = 1,
    long FeeReserve = 0,
    float RetentionSeconds = 604_800f
) {
    /// <summary>Gets the empty section — every composer's fallback for a document that declared no <c>market</c>
    /// section at all (<c>current.Market ?? Empty</c>, the identical pattern <c>groups</c> uses).</summary>
    public static WorldMarketSection Empty { get; } = new();

    /// <summary>Gets the formats this market admits — <see cref="Formats"/> if authored, otherwise both. A computed
    /// convenience, deliberately absent from the document contract (<see cref="JsonIgnoreAttribute"/>) and from
    /// <c>market.schema.json</c> — <see cref="Formats"/> alone is the authored fact; re-deriving this from it at
    /// every read site (the compose arms, <c>world.market</c>'s own read-back) is what the property exists to save,
    /// never a second wire-carried source of truth for the same question.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldMarketFormat> EffectiveFormats => (Formats ?? [WorldMarketFormat.English, WorldMarketFormat.Buyout]);
}

/// <summary>The <see cref="WorldMarketSection"/> caps read by <see cref="WorldDefinitionValidator"/>.</summary>
public static class WorldMarketCapacity {
    /// <summary>The section's live listing-count ceiling (all statuses combined) — bounded in practice by
    /// <see cref="WorldMarketSection.RetentionSeconds"/>: an active row never counts against retention, but a
    /// terminal one ages out of this ceiling once the engine's own sweep archives it (see
    /// <see cref="WorldMarketSection.RetentionSeconds"/>'s remarks) — a market that resolves nothing still fills at
    /// exactly this many active listings.</summary>
    public const int MaxListings = 256;

    /// <summary>The section's declared admission-tier-count ceiling.</summary>
    public const int MaxAdmissionTiers = 8;

    /// <summary>The greatest <see cref="WorldMarketSection.FeeBasisPoints"/> a market may declare (20%).</summary>
    public const int MaxFeeBasisPoints = 2_000;

    /// <summary>The least legal <see cref="WorldMarketSection.MinDurationSeconds"/>/<see cref="WorldMarketSection.MaxDurationSeconds"/>.</summary>
    public const float MinDurationFloorSeconds = 1f;

    /// <summary>The greatest legal <see cref="WorldMarketSection.MaxDurationSeconds"/> (30 days).</summary>
    public const float MaxDurationCeilingSeconds = 2_592_000f;

    /// <summary>The least legal <see cref="WorldMarketSection.RetentionSeconds"/>.</summary>
    public const float MinRetentionSeconds = 1f;

    /// <summary>The greatest legal <see cref="WorldMarketSection.RetentionSeconds"/> (30 days).</summary>
    public const float MaxRetentionSeconds = 2_592_000f;
}
