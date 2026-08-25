using System.Globalization;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The local auction-house console surface — <c>market.list</c>/<c>.bid</c>/<c>.buyout</c>/<c>.cancel</c> submit the
/// live listing/bid/settle machinery over <see cref="WorldMutation.CreateMarketListing"/>/
/// <see cref="WorldMutation.PlaceMarketBid"/>/<see cref="WorldMutation.BuyoutMarketListing"/>/
/// <see cref="WorldMutation.CancelMarketListing"/>; <c>world.market</c> is the read-back.
/// </summary>
/// <remarks>
/// Each mutating verb takes an explicit leading trade-party token (<c>&lt;seller&gt;</c>/<c>&lt;bidder&gt;</c>/
/// <c>&lt;buyer&gt;</c>/<c>&lt;canceler&gt;</c>, the same <c>seat1..seat4|peer:&lt;n&gt;:&lt;generation&gt;</c>
/// grammar <c>world.grant</c>/<c>world.ownership.offer</c> already use) — the same checked-authority/trade-party
/// split <c>world.group.join &lt;group-id&gt; &lt;principal&gt;</c> rides: the mutation's own <c>Principal</c> stays
/// <c>context.ActingPrincipal()</c> (stdin is always Console; never constructed), and the token names who the trade
/// moves fact quantity for — reachable here because stdin's Console identity is one of the two the server's own
/// party-authority check admits naming any seat/peer for; a live seat/peer connection submitting the identical
/// mutation kind over another door may only ever name itself (see <c>Server.WorldServer.TryAuthorizeMarketParty</c>).
/// A real connected client acting for itself passes its own principal as the token.
/// A listing that reaches its own deadline settles or expires on its own — <see cref="WorldMutation.SettleMarketListing"/>
/// is never reachable from this surface (see that mutation's remarks). Every mutating verb routes
/// <see cref="CommandRouting.Simulation"/> and returns <see cref="CommandResult.None"/> — the server prints the loud
/// <c>[world.mutation: … applied/rejected]</c> line.
/// </remarks>
public sealed class WorldMarketCommandModule(IWorldConsoleAuthority authority, IServerLink link) : ICommandModule {
    // Narrower than WorldPrincipal.TokenGrammar: a market party is a seat or peer only (see this class's own
    // remarks) — console/world/addon/document/group all parse as a WorldPrincipal but are refused as a trade party,
    // so the parse-failure refusal must not advertise them as legal here.
    private const string PartyTokenGrammar = "seat1..seat4|peer:<n>:<generation>";

    // The read-back: config first (when unfiltered), then every live listing (id-filtered when requested).
    private static string Describe(WorldDefinition definition, long? filter) {
        if (definition.Market is not { } market) {
            return "[world.market: (no market section)]";
        }

        var echo = CommandEcho.Open(verb: "world.market");

        if (filter is null) {
            _ = echo.Field(key: "formats", value: $"[{string.Join(
                separator: ',',
                values: market.EffectiveFormats
            )}]")
                .Field(key: "feeBasisPoints", value: market.FeeBasisPoints)
                .Field(key: "duration", value: $"[{market.MinDurationSeconds.ToString(provider: CultureInfo.InvariantCulture)}..{market.MaxDurationSeconds.ToString(provider: CultureInfo.InvariantCulture)}]")
                .Field(key: "retentionSeconds", value: market.RetentionSeconds)
                .Field(key: "feeReserve", value: market.FeeReserve)
                .Segment();
        }

        foreach (var listing in (market.Listings ?? [])) {
            if (
                (filter is { } only) &&
                (listing.Id != only)
            ) {
                continue;
            }

            _ = echo.Head(head: "listing")
                .Field(key: "id", value: listing.Id)
                .Field(key: "seller", value: listing.Seller.Describe())
                .Field(key: "item", value: $"{listing.Quantity}x{listing.ItemRow.Value}")
                .Field(key: "currency", value: listing.CurrencyRow.Value)
                .Field(key: "format", value: listing.Format.ToString())
                .Field(key: "startPrice", value: listing.StartPrice);

            if (listing.BuyoutPrice is { } buyoutPrice) {
                _ = echo.Field(key: "buyoutPrice", value: buyoutPrice);
            }

            _ = echo.Field(key: "deadlineTick", value: listing.DeadlineTick)
                .Field(key: "status", value: listing.Status.ToString())
                .Field(key: "currentBid", value: listing.CurrentBid);

            if (listing.CurrentBidder is { } bidder) {
                _ = echo.Field(key: "currentBidder", value: bidder.Describe());
            }

            if (listing.ResolvedTick is { } resolvedTick) {
                _ = echo.Field(key: "resolvedTick", value: resolvedTick);
            }

            _ = echo.Segment();
        }

        return echo.Close();
    }
    private static bool TryFormat(ReadOnlySpan<char> token, out WorldMarketFormat format) {
        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "english"
        )) {
            format = WorldMarketFormat.English;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "buyout"
        )) {
            format = WorldMarketFormat.Buyout;

            return true;
        }

        format = default;

        return false;
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "market.list",
            description: "Lists <quantity> of <itemRow> for sale on behalf of <seller>, escrowing it out of their own cell: market.list <seller> <itemRow> <quantity> <currencyRow> <english|buyout> <startPrice> <buyoutPrice> <durationSeconds>. <seller> is a principal token (seat1..seat4|peer:<n>:<generation>). <buyoutPrice> 0 means none (legal for english; buyout requires a positive value). <startPrice> is unused by buyout (pass 0). Rejected loudly when the world authors no market section, <seller> is not a seat or peer, the format is not admitted, the duration falls outside the market's bounds, either row is not a declared capacity-bounded int state row, or the seller holds fewer than <quantity>. Buffers and applies at the tick boundary under Mutate/section:market.",
            handler: (context, args) => {
                if (args.Count != 8) {
                    return CommandResult.Usage(
                        form: "<seller> <itemRow> <quantity> <currencyRow> <english|buyout> <startPrice> <buyoutPrice> <durationSeconds>",
                        verb: "market.list"
                    );
                }

                if (!WorldGrantCommandModule.TryParsePrincipal(
                    token: args[0],
                    principal: out var seller
                )) {
                    return CommandResult.Error(output: $"[market.list: unknown principal '{args[0].ToString()}' — {PartyTokenGrammar}]");
                }

                if (!WorldCellName.TryParse(
                    candidate: args[1].ToString(),
                    name: out var itemRow,
                    reason: out var itemReason
                )) {
                    return CommandResult.Error(output: $"[market.list: itemRow '{args[1].ToString()}' {itemReason}]");
                }

                if (!args.TryLong(
                    index: 2,
                    value: out var quantity
                )) {
                    return CommandResult.Error(output: $"[market.list: '{args[2].ToString()}' is not an integer quantity]");
                }

                if (!WorldCellName.TryParse(
                    candidate: args[3].ToString(),
                    name: out var currencyRow,
                    reason: out var currencyReason
                )) {
                    return CommandResult.Error(output: $"[market.list: currencyRow '{args[3].ToString()}' {currencyReason}]");
                }

                if (!TryFormat(
                    token: args[4],
                    format: out var format
                )) {
                    return CommandResult.Error(output: $"[market.list: unknown format '{args[4].ToString()}' — english|buyout]");
                }

                if (!args.TryLong(
                    index: 5,
                    value: out var startPrice
                )) {
                    return CommandResult.Error(output: $"[market.list: '{args[5].ToString()}' is not an integer startPrice]");
                }

                if (!args.TryLong(
                    index: 6,
                    value: out var buyoutPrice
                )) {
                    return CommandResult.Error(output: $"[market.list: '{args[6].ToString()}' is not an integer buyoutPrice]");
                }

                if (!args.TryFloat(
                    index: 7,
                    value: out var durationSeconds
                )) {
                    return CommandResult.Error(output: $"[market.list: '{args[7].ToString()}' is not a durationSeconds]");
                }

                link.SubmitWorldMutation(mutation: new WorldMutation.CreateMarketListing(
                    Principal: context.ActingPrincipal(),
                    Seller: seller,
                    ItemRow: itemRow,
                    Quantity: quantity,
                    CurrencyRow: currencyRow,
                    Format: format,
                    StartPrice: startPrice,
                    BuyoutPrice: ((buyoutPrice > 0)
                    ? buyoutPrice
                    : null),
                    DurationSeconds: durationSeconds
                ));

                return CommandResult.None;
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "market.bid",
            description: "Places an ascending bid against an english listing on behalf of <bidder>, escrowing <amount> out of their own currency cell and refunding any standing bidder: market.bid <bidder> <listingId> <amount>. Rejected loudly when the listing does not exist, is not active, has reached its deadline, is not english, <bidder> is the listing's own seller or not a seat/peer, <amount> does not strictly exceed the current bid (or the listing's startPrice while unbid), or the bidder cannot afford it. Buffers and applies at the tick boundary under Mutate/section:market.",
            handler: (context, args) => {
                if (args.Count != 3) {
                    return CommandResult.Usage(
                        form: "<bidder> <listingId> <amount>",
                        verb: "market.bid"
                    );
                }

                if (!WorldGrantCommandModule.TryParsePrincipal(
                    token: args[0],
                    principal: out var bidder
                )) {
                    return CommandResult.Error(output: $"[market.bid: unknown principal '{args[0].ToString()}' — {PartyTokenGrammar}]");
                }

                if (!args.TryLong(
                    index: 1,
                    value: out var listingId
                )) {
                    return CommandResult.Error(output: $"[market.bid: '{args[1].ToString()}' is not an integer listingId]");
                }

                if (!args.TryLong(
                    index: 2,
                    value: out var amount
                )) {
                    return CommandResult.Error(output: $"[market.bid: '{args[2].ToString()}' is not an integer amount]");
                }

                link.SubmitWorldMutation(mutation: new WorldMutation.PlaceMarketBid(
                    Principal: context.ActingPrincipal(),
                    Bidder: bidder,
                    ListingId: listingId,
                    Amount: amount
                ));

                return CommandResult.None;
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "market.buyout",
            description: "Settles a listing immediately at its declared buyoutPrice on behalf of <buyer>: market.buyout <buyer> <listingId>. Pays the seller net of the market's fee, refunds any standing english bidder, and credits the buyer's item cell. Rejected loudly when the listing does not exist, is not active, has reached its deadline, declares no buyoutPrice, <buyer> is the listing's own seller or not a seat/peer, or the buyer cannot afford it. Buffers and applies at the tick boundary under Mutate/section:market.",
            handler: (context, args) => {
                if (args.Count != 2) {
                    return CommandResult.Usage(
                        form: "<buyer> <listingId>",
                        verb: "market.buyout"
                    );
                }

                if (!WorldGrantCommandModule.TryParsePrincipal(
                    token: args[0],
                    principal: out var buyer
                )) {
                    return CommandResult.Error(output: $"[market.buyout: unknown principal '{args[0].ToString()}' — {PartyTokenGrammar}]");
                }

                if (!args.TryLong(
                    index: 1,
                    value: out var listingId
                )) {
                    return CommandResult.Error(output: $"[market.buyout: '{args[1].ToString()}' is not an integer listingId]");
                }

                link.SubmitWorldMutation(mutation: new WorldMutation.BuyoutMarketListing(
                    Principal: context.ActingPrincipal(),
                    Buyer: buyer,
                    ListingId: listingId
                ));

                return CommandResult.None;
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "market.cancel",
            description: "Withdraws a listing before it settles on behalf of <canceler>, returning the escrowed item to the seller and refunding any standing bidder: market.cancel <canceler> <listingId>. Rejected loudly when the listing does not exist, is not active, or <canceler> is not its seller. Buffers and applies at the tick boundary under Mutate/section:market.",
            handler: (context, args) => {
                if (args.Count != 2) {
                    return CommandResult.Usage(
                        form: "<canceler> <listingId>",
                        verb: "market.cancel"
                    );
                }

                if (!WorldGrantCommandModule.TryParsePrincipal(
                    token: args[0],
                    principal: out var canceler
                )) {
                    return CommandResult.Error(output: $"[market.cancel: unknown principal '{args[0].ToString()}' — {PartyTokenGrammar}]");
                }

                if (!args.TryLong(
                    index: 1,
                    value: out var listingId
                )) {
                    return CommandResult.Error(output: $"[market.cancel: '{args[1].ToString()}' is not an integer listingId]");
                }

                link.SubmitWorldMutation(mutation: new WorldMutation.CancelMarketListing(
                    Principal: context.ActingPrincipal(),
                    Canceler: canceler,
                    ListingId: listingId
                ));

                return CommandResult.None;
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.market",
            description: "Echoes the market section — config (formats, feeBasisPoints, duration bounds, retention, and fee reserve) and the live listing ledger: world.market [listingId]. With a listing id, echoes only that listing.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Usage(
                        form: "[listingId]",
                        verb: "world.market"
                    );
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.market"
                )) {
                    return error;
                }

                long? filter = null;

                if (args.Count == 1) {
                    if (!args.TryLong(
                        index: 0,
                        value: out var id
                    )) {
                        return CommandResult.Error(output: $"[world.market: '{args[0].ToString()}' is not an integer listingId]");
                    }

                    filter = id;
                }

                return new CommandResult(Output: Describe(
                    definition: server.Definition,
                    filter: filter
                ));
            }
        );
    }
}
