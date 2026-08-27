using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The local auction house's own fixture layer — extends <see cref="Fixtures.BuildDocument"/> with a <c>gold</c>
/// currency row and an <c>apple</c> item row (both keyed by the holder's 0-based entity index — the fact vocabulary
/// <see cref="WorldMutation.CreateMarketListing"/> and its siblings move quantity through), and a <c>market</c>
/// section admitting both formats. Seat 0 is the seller fixture; seats 1/2 are bidder fixtures.
/// </summary>
internal static class MarketFixtures {
    /// <summary>The currency row every market law in this suite prices against.</summary>
    public static readonly WorldCellName GoldRow = WorldCellName.Parse(candidate: "gold");
    /// <summary>The item row every market law in this suite trades.</summary>
    public static readonly WorldCellName AppleRow = WorldCellName.Parse(candidate: "apple");

    /// <summary>The house fee — 10%, chosen so a fee amount is exact and easy to hand-verify against any bid.</summary>
    public const int FeeBasisPoints = 1_000;
    /// <summary>The fixture market's declared minimum listing duration — the shortest legal duration, so a law that
    /// needs a listing to reach its deadline steps the fewest ticks.</summary>
    public const float MinDurationSeconds = 1f;
    public const long BidderStartingGold = 500;
    public const float MaxDurationSeconds = 3_600f;
    public const long SellerStartingApples = 10;
    public const long SellerStartingGold = 500;

    /// <summary>Builds one keyed-Int market row — the shape every market fixture shares — carrying
    /// <paramref name="balances"/> keyed by 0-based holder index.</summary>
    public static WorldStateRow HolderRow(WorldCellName name, params long[] balances) => new(
        Name: name,
        Kind: CellKind.Int,
        Capacity: 128,
        NonNegative: true,
        Cells: [.. balances.Select(selector: static (value, index) => new WorldStateCell(
            Key: WorldCellName.Parse(candidate: index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
            Value: value
        ))]
    );
    /// <summary>Builds the <c>market</c> section every market fixture shares: both formats admitted, the fixture
    /// duration window, and the schema's own retention default unless <paramref name="retentionSeconds"/> names
    /// one.</summary>
    public static WorldMarketSection Section(int feeBasisPoints = FeeBasisPoints, float? retentionSeconds = null) {
        var section = new WorldMarketSection(
            Formats: [WorldMarketFormat.English, WorldMarketFormat.Buyout],
            FeeBasisPoints: feeBasisPoints,
            MinDurationSeconds: MinDurationSeconds,
            MaxDurationSeconds: MaxDurationSeconds
        );

        return ((retentionSeconds is float seconds) ? (section with { RetentionSeconds = seconds }) : section);
    }
    /// <summary>Builds the market-flavored document: <see cref="Fixtures.BuildDocument"/> plus the <c>gold</c>/<c>apple</c>
    /// state rows and a <c>market</c> section admitting both formats. <paramref name="bidderStartingGold"/> sets
    /// seat 1's balance alone; seat 2 always holds <see cref="BidderStartingGold"/>.</summary>
    public static WorldDefinition BuildDocument(long bidderStartingGold = BidderStartingGold) {
        var gold = HolderRow(name: GoldRow, balances: [SellerStartingGold, bidderStartingGold, BidderStartingGold]);
        var apple = HolderRow(name: AppleRow, balances: [SellerStartingApples]);

        return (Fixtures.BuildDocument().WithWorldState(rows: [gold, apple]) with { Market = Section() });
    }
    /// <summary>Reads a principal's cell value out of a keyed state row, defaulting to zero when the row declares no
    /// cell for that holder — the same convention the market compose arms themselves read through.</summary>
    public static long CellValueOf(WorldDefinition definition, WorldCellName row, WorldPrincipal principal) {
        var stateRow = WorldDefinitionRows.FindStateRow(rows: definition.State, name: row);

        if (stateRow is null) {
            return 0L;
        }

        var key = principal.Index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);

        foreach (var cell in (stateRow.Cells ?? [])) {
            if (string.Equals(a: cell.Key.Value, b: key, comparisonType: System.StringComparison.Ordinal)) {
                return cell.Value;
            }
        }

        return 0L;
    }
    /// <summary>Finds a listing by id, or <see langword="null"/> when none exists.</summary>
    public static WorldMarketListing? FindListing(WorldDefinition definition, long id) {
        foreach (var listing in (definition.Market?.Listings ?? [])) {
            if (listing.Id == id) {
                return listing;
            }
        }

        return null;
    }
}
