using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private static WorldMarketListing? FindMarketListing(IReadOnlyList<WorldMarketListing> listings, long id) {
        foreach (var listing in listings) {
            if (listing.Id == id) {
                return listing;
            }
        }

        return null;
    }
    // The (row, key) pairs a market mutation actually wrote — derived the same way each TryCompose*Market* arm
    // derives its own keys (TryPlayerCellKey off each party's principal), reading `original`'s pre-write listing for
    // a bid/buyout/cancel/settle since `candidate` already carries this mutation's own write (in particular,
    // PlaceMarketBid's previous bidder is only findable in the listing as it stood before this bid replaced it).
    // Returns null for every non-market mutation kind, distinguishing "not a market mutation" from "a market
    // mutation that happens to touch nothing" (an empty list) — the latter can occur when a party is somehow not a
    // seat/peer at this point (defensive; TryCompose already refused before this ever runs).
    private static IReadOnlyList<MarketCellTouch>? MarketCellTouches(WorldDefinition original, WorldMutation mutation) {
        switch (mutation) {
            case WorldMutation.CreateMarketListing m: {
                    if (!TryPlayerCellKey(
                        principal: m.Seller,
                        key: out var sellerKey
                    )) {
                        return [];
                    }

                    return [new MarketCellTouch(
                            Row: m.ItemRow,
                            Key: sellerKey
                        )];
                }
            case WorldMutation.PlaceMarketBid m: {
                    if (FindMarketListing(
                        listings: (original.Market?.Listings ?? []),
                        id: m.ListingId
                    ) is not { } listing) {
                        return [];
                    }

                    var touches = new List<MarketCellTouch>(capacity: 2);

                    if (TryPlayerCellKey(
                        principal: m.Bidder,
                        key: out var bidderKey
                    )) {
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.CurrencyRow,
                            Key: bidderKey
                        ));
                    }

                    if (
                        (listing.CurrentBidder is { } previous) &&
                        TryPlayerCellKey(
                        key: out var previousKey,
                        principal: previous
                    )
                    ) {
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.CurrencyRow,
                            Key: previousKey
                        ));
                    }

                    return touches;
                }
            case WorldMutation.BuyoutMarketListing m: {
                    if (FindMarketListing(
                        listings: (original.Market?.Listings ?? []),
                        id: m.ListingId
                    ) is not { } listing) {
                        return [];
                    }

                    var touches = new List<MarketCellTouch>(capacity: 4);

                    if (TryPlayerCellKey(
                        principal: m.Buyer,
                        key: out var buyerKey
                    )) {
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.CurrencyRow,
                            Key: buyerKey
                        ));
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.ItemRow,
                            Key: buyerKey
                        ));
                    }

                    if (TryPlayerCellKey(
                        principal: listing.Seller,
                        key: out var sellerKey
                    )) {
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.CurrencyRow,
                            Key: sellerKey
                        ));
                    }

                    if (
                        (listing.CurrentBidder is { } previous) &&
                        (previous != m.Buyer) &&
                        TryPlayerCellKey(
                        key: out var previousKey,
                        principal: previous
                    )
                    ) {
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.CurrencyRow,
                            Key: previousKey
                        ));
                    }

                    return touches;
                }
            case WorldMutation.CancelMarketListing m: {
                    if (FindMarketListing(
                        listings: (original.Market?.Listings ?? []),
                        id: m.ListingId
                    ) is not { } listing) {
                        return [];
                    }

                    var touches = new List<MarketCellTouch>(capacity: 2);

                    if (TryPlayerCellKey(
                        principal: listing.Seller,
                        key: out var sellerKey
                    )) {
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.ItemRow,
                            Key: sellerKey
                        ));
                    }

                    if (
                        (listing.CurrentBidder is { } bidder) &&
                        TryPlayerCellKey(
                        key: out var bidderKey,
                        principal: bidder
                    )
                    ) {
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.CurrencyRow,
                            Key: bidderKey
                        ));
                    }

                    return touches;
                }
            case WorldMutation.SettleMarketListing m: {
                    if (FindMarketListing(
                        listings: (original.Market?.Listings ?? []),
                        id: m.ListingId
                    ) is not { } listing) {
                        return [];
                    }

                    var touches = new List<MarketCellTouch>(capacity: 2);

                    if (
                        (listing.CurrentBidder is { } winner) &&
                        TryPlayerCellKey(
                        key: out var winnerKey,
                        principal: winner
                    ) &&
                        TryPlayerCellKey(
                        principal: listing.Seller,
                        key: out var winSellerKey
                    )
                    ) {
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.CurrencyRow,
                            Key: winSellerKey
                        ));
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.ItemRow,
                            Key: winnerKey
                        ));
                    } else if (TryPlayerCellKey(
                        principal: listing.Seller,
                        key: out var expiredSellerKey
                    )) {
                        touches.Add(item: new MarketCellTouch(
                            Row: listing.ItemRow,
                            Key: expiredSellerKey
                        ));
                    }

                    return touches;
                }
            default:
                return null;
        }
    }
    // The house fee on a settled amount, in basis points — bounded well inside `long` (amount is capped at
    // WorldStateCapacity.MaxIntCellValue and feeBasisPoints at WorldMarketCapacity.MaxFeeBasisPoints, so the
    // intermediate product can never overflow).
    private static long MarketFee(long amount, int feeBasisPoints) => ((amount * feeBasisPoints) / 10_000L);
    // Market retention sweep — the same "recovery is a lifetime rule" shape ReclaimExpiredEscrows/
    // SettleExpiredMarketListings establish: once a terminal row has stood past market.retentionSeconds, fires
    // exactly one PruneMarketListings mutation (never one per row — its own compose arm removes every eligible row
    // in the same candidate) under WorldPrincipal.World. Checked here, before submitting, so a quiescent market with
    // nothing yet eligible never drives a mutation that would only compose to a loud no-op refusal every tick — the
    // identical reason SettleExpiredMarketListings/ReclaimExpiredEscrows pre-filter their own loops.
    private void PruneExpiredMarketListings(ulong tick) {
        var market = (m_definition.Market ?? WorldMarketSection.Empty);

        if (m_definition.SimulationRateHz <= 0) {
            return;
        }

        var retentionTicks = unchecked((long)WorldSimulationTickConversion.DurationTicks(
            seconds: market.RetentionSeconds,
            ratePerSecond: ((uint)m_definition.SimulationRateHz)
        ));

        foreach (var listing in (market.Listings ?? [])) {
            if (
                (listing.Status != WorldMarketListingStatus.Active) &&
                (listing.ResolvedTick is { } resolvedTick) &&
                (unchecked((long)tick) >= unchecked((resolvedTick + retentionTicks)))
            ) {
                _ = TryApplyMutation(
                    mutation: new WorldMutation.PruneMarketListings(Principal: WorldPrincipal.World),
                    tick: tick,
                    connectionId: SubmissionEnvelope.LocalConnectionId,
                    correlationId: 0,
                    preMetered: false
                );

                return;
            }
        }
    }
    // Reads a market fact cell, defaulting to zero for a holder who has never traded — the SAME "absent key ==
    // zero" convention UpsertStateCell's own Add operand already follows.
    private static long ReadMarketCellValue(WorldDefinition definition, WorldCellName row, string key, ulong tick) {
        _ = WorldStateReader.TryRead(
            definition: definition,
            key: key,
            rawValue: out var raw,
            row: out _,
            rowName: row,
            text: out _,
            tick: tick
        );

        return (raw ?? 0L);
    }
    // ESCROW RECOVERY — the "recovery is a LIFETIME RULE" half of the escrow/transfer lane, run every tick right
    // beside world-rule evaluation (deterministic, tick-driven, no wall clock — the SAME $tick unit a rule's own
    // gate would compare against). Fires an ordinary SettleOwnership(Reclaim: true) under WorldPrincipal.World — the
    // SAME structural-exemption door a rule's own effects use (Server.WorldServer.TryAdmitMutation admits it before
    // the grant table is even consulted) — for every subject whose escrow has reached its DeadlineTick with no
    // accept. Recovery therefore needs no operator action: the offerer gets the subject back the tick the deadline
    // passes, exactly as if a world-authored rule had reclaimed it. `ownership` is read once, before any mutation in
    // this pass swaps `m_definition` — an IReadOnlyList this project never mutates in place (every write rebuilds a
    // new list via Upsert), so iterating the pre-sweep snapshot while TryApplyMutation installs later candidates is
    // safe; a subject an earlier iteration already reclaimed this tick simply is not read again.
    private void ReclaimExpiredEscrows(ulong tick) {
        var ownership = (m_definition.Groups ?? WorldGroupsSection.Empty).Ownership;

        foreach (var row in ownership) {
            if (
                (row.Owner.Kind == OwnershipOwnerKind.Escrow) &&
                (row.Owner.Escrow is { } escrow) &&
                (unchecked((long)tick) >= escrow.DeadlineTick)
            ) {
                _ = TryApplyMutation(
                    mutation: new WorldMutation.SettleOwnership(
                        Principal: WorldPrincipal.World,
                        Subject: row.Subject,
                        Reclaim: true
                    ),
                    tick: tick,
                    connectionId: SubmissionEnvelope.LocalConnectionId,
                    correlationId: 0,
                    preMetered: false
                );
            }
        }
    }
    // MARKET DEADLINE RECOVERY — the SAME "recovery is a LIFETIME RULE" shape ReclaimExpiredEscrows establishes,
    // fired right beside it: an Active listing whose DeadlineTick has passed settles (a standing bid wins) or
    // expires (no bid ever landed) with no operator action, under WorldPrincipal.World, the identical structural
    // exemption a rule effect's own writes use. `listings` is read once, before any mutation in this pass swaps
    // m_definition, matching ReclaimExpiredEscrows' own safe-iteration remark.
    private void SettleExpiredMarketListings(ulong tick) {
        var listings = ((m_definition.Market ?? WorldMarketSection.Empty).Listings ?? []);

        foreach (var listing in listings) {
            if (
                (listing.Status == WorldMarketListingStatus.Active) &&
                (unchecked((long)tick) >= listing.DeadlineTick)
            ) {
                _ = TryApplyMutation(
                    mutation: new WorldMutation.SettleMarketListing(
                        Principal: WorldPrincipal.World,
                        ListingId: listing.Id
                    ),
                    tick: tick,
                    connectionId: SubmissionEnvelope.LocalConnectionId,
                    correlationId: 0,
                    preMetered: false
                );
            }
        }
    }
    // The trade-party authority split every market mutation checks beneath the coarse Mutate/section:market hold:
    // the acting principal may name itself as the trade party (a real connected client acting for itself) or,
    // narrowly, Console may name any seat/peer — the split stdin's own Console-stamped, seat-naming submissions rely
    // on. A seat's own boot-seeded Mutate/section:market hold is authority to trade its own inventory, never another
    // seat's — a hold over the section was never a hold over every party inside it, the same distinction the
    // row-scoped Edit/state:&lt;row&gt; check draws for state writes. WorldPrincipal.World is exempt for the same
    // reason Console is (both are trusted, structural or operator identities that never impersonate a live player),
    // even though no engine sweep constructs a market mutation naming a party today.
    private static bool TryAuthorizeMarketParty(WorldPrincipal actingPrincipal, WorldPrincipal party, out string reason) {
        if (
            (actingPrincipal == party) ||
            (actingPrincipal.Kind is PrincipalKind.Console or PrincipalKind.World)
        ) {
            reason = string.Empty;

            return true;
        }

        reason = $"{actingPrincipal.Describe()} may not act as trade party {party.Describe()} — only Console or {party.Describe()} itself may name it";

        return false;
    }
    // market.buyout — settles a listing immediately at its declared BuyoutPrice: pays the seller net of fee, refunds
    // any standing English bidder, credits the buyer's item cell, all in the SAME candidate.
    private static bool TryComposeBuyoutMarketListing(WorldDefinition current, WorldMutation.BuyoutMarketListing mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        var market = (current.Market ?? WorldMarketSection.Empty);

        if (FindMarketListing(
            listings: (market.Listings ?? []),
            id: mutation.ListingId
        ) is not { } listing) {
            reason = $"no listing #{mutation.ListingId}";

            return false;
        }

        if (listing.Status != WorldMarketListingStatus.Active) {
            reason = $"listing #{mutation.ListingId} is {listing.Status}, not active";

            return false;
        }

        if (unchecked((long)tick) >= listing.DeadlineTick) {
            reason = $"listing #{mutation.ListingId} has reached its deadline";

            return false;
        }

        if (listing.BuyoutPrice is not { } buyoutPrice) {
            reason = $"listing #{mutation.ListingId} declares no buyoutPrice";

            return false;
        }

        if (!TryPlayerCellKey(
            principal: mutation.Buyer,
            key: out var buyerKey
        )) {
            reason = $"buyer {mutation.Buyer.Describe()} must be a seat or peer";

            return false;
        }

        if (!TryAuthorizeMarketParty(
            actingPrincipal: mutation.Principal,
            party: mutation.Buyer,
            reason: out reason
        )) {
            return false;
        }

        if (mutation.Buyer == listing.Seller) {
            reason = "the seller may not buy out their own listing";

            return false;
        }

        if (!TryPlayerCellKey(
            principal: listing.Seller,
            key: out var sellerKey
        )) {
            reason = "listing seller is not a seat or peer";

            return false;
        }

        // A standing bidder buying themselves out only owes the difference — their own escrowed bid is refunded
        // and re-spent in the SAME move, never round-tripped through a separate refund the caller could observe.
        var refundToSelf = ((listing.CurrentBidder == mutation.Buyer)
            ? listing.CurrentBid
            : 0L
        );
        var buyerBalance = ReadMarketCellValue(
            definition: current,
            row: listing.CurrencyRow,
            key: buyerKey,
            tick: tick
        );
        var effectiveCost = (buyoutPrice - refundToSelf);

        if (buyerBalance < effectiveCost) {
            reason = $"buyer holds {buyerBalance} of '{listing.CurrencyRow}', short of the {effectiveCost} needed";

            return false;
        }

        var state = WriteMarketCell(
            rows: current.State,
            rowName: listing.CurrencyRow,
            key: buyerKey,
            value: (buyerBalance - effectiveCost)
        );

        if (
            (listing.CurrentBidder is { } previousBidder) &&
            (previousBidder != mutation.Buyer) &&
            TryPlayerCellKey(
            key: out var previousKey,
            principal: previousBidder
        )
        ) {
            var previousBalance = ReadMarketCellValue(
                definition: current.WithWorldState(rows: state),
                row: listing.CurrencyRow,
                key: previousKey,
                tick: tick
            );

            state = WriteMarketCell(
                rows: state,
                rowName: listing.CurrencyRow,
                key: previousKey,
                value: (previousBalance + listing.CurrentBid)
            );
        }

        var fee = MarketFee(
            amount: buyoutPrice,
            feeBasisPoints: market.FeeBasisPoints
        );
        var sellerBalance = ReadMarketCellValue(
            definition: current.WithWorldState(rows: state),
            row: listing.CurrencyRow,
            key: sellerKey,
            tick: tick
        );

        state = WriteMarketCell(
            rows: state,
            rowName: listing.CurrencyRow,
            key: sellerKey,
            value: (sellerBalance + (buyoutPrice - fee))
        );

        var buyerItemBalance = ReadMarketCellValue(
            definition: current.WithWorldState(rows: state),
            row: listing.ItemRow,
            key: buyerKey,
            tick: tick
        );

        state = WriteMarketCell(
            rows: state,
            rowName: listing.ItemRow,
            key: buyerKey,
            value: (buyerItemBalance + listing.Quantity)
        );

        var updatedListing = (listing with { Status = WorldMarketListingStatus.Settled, ResolvedTick = unchecked((long)tick) });

        candidate = (current.WithWorldState(rows: state) with {
            Market = (market with {
                Listings = Upsert(
            list: (market.Listings ?? []),
            item: updatedListing,
            keyOf: static (WorldMarketListing l) => l.Id
        ),
                FeeReserve = (market.FeeReserve + fee),
            }),
        });

        return true;
    }
    // market.cancel — withdraws a listing, returning the escrowed item to the seller and refunding any standing
    // English bidder, all in the SAME candidate. Seller-only.
    private static bool TryComposeCancelMarketListing(WorldDefinition current, WorldMutation.CancelMarketListing mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        var market = (current.Market ?? WorldMarketSection.Empty);

        if (FindMarketListing(
            listings: (market.Listings ?? []),
            id: mutation.ListingId
        ) is not { } listing) {
            reason = $"no listing #{mutation.ListingId}";

            return false;
        }

        if (listing.Status != WorldMarketListingStatus.Active) {
            reason = $"listing #{mutation.ListingId} is {listing.Status}, not active";

            return false;
        }

        if (!TryAuthorizeMarketParty(
            actingPrincipal: mutation.Principal,
            party: mutation.Canceler,
            reason: out reason
        )) {
            return false;
        }

        if (mutation.Canceler != listing.Seller) {
            reason = $"only the seller {listing.Seller.Describe()} may cancel listing #{mutation.ListingId}";

            return false;
        }

        if (!TryPlayerCellKey(
            principal: listing.Seller,
            key: out var sellerKey
        )) {
            reason = "listing seller is not a seat or peer";

            return false;
        }

        var sellerItemBalance = ReadMarketCellValue(
            definition: current,
            row: listing.ItemRow,
            key: sellerKey,
            tick: tick
        );
        var state = WriteMarketCell(
            rows: current.State,
            rowName: listing.ItemRow,
            key: sellerKey,
            value: (sellerItemBalance + listing.Quantity)
        );

        if (
            (listing.CurrentBidder is { } bidder) &&
            TryPlayerCellKey(
            key: out var bidderKey,
            principal: bidder
        )
        ) {
            var bidderBalance = ReadMarketCellValue(
                definition: current.WithWorldState(rows: state),
                row: listing.CurrencyRow,
                key: bidderKey,
                tick: tick
            );

            state = WriteMarketCell(
                rows: state,
                rowName: listing.CurrencyRow,
                key: bidderKey,
                value: (bidderBalance + listing.CurrentBid)
            );
        }

        var updatedListing = (listing with { Status = WorldMarketListingStatus.Cancelled, ResolvedTick = unchecked((long)tick) });
        var newMarket = (market with {
            Listings = Upsert(
            list: (market.Listings ?? []),
            item: updatedListing,
            keyOf: static (WorldMarketListing l) => l.Id
        ),
        });

        candidate = (current.WithWorldState(rows: state) with { Market = newMarket });

        return true;
    }
    // market.list — escrows Quantity out of the seller's own ItemRow cell atomically with minting the listing row.
    private static bool TryComposeCreateMarketListing(WorldDefinition current, WorldMutation.CreateMarketListing mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        if (current.Market is not { } market) {
            reason = "this world authors no market section";

            return false;
        }

        if (!TryPlayerCellKey(
            principal: mutation.Seller,
            key: out var sellerKey
        )) {
            reason = $"seller {mutation.Seller.Describe()} must be a seat or peer";

            return false;
        }

        if (!TryAuthorizeMarketParty(
            actingPrincipal: mutation.Principal,
            party: mutation.Seller,
            reason: out reason
        )) {
            return false;
        }

        var admitted = false;

        foreach (var format in market.EffectiveFormats) {
            if (format == mutation.Format) {
                admitted = true;

                break;
            }
        }

        if (!admitted) {
            reason = $"market does not admit format '{mutation.Format}'";

            return false;
        }

        if (
            !float.IsFinite(f: mutation.DurationSeconds) ||
            (mutation.DurationSeconds < market.MinDurationSeconds) ||
            (mutation.DurationSeconds > market.MaxDurationSeconds)
        ) {
            reason = $"durationSeconds {mutation.DurationSeconds} is outside {market.MinDurationSeconds}..{market.MaxDurationSeconds}";

            return false;
        }

        if (mutation.Quantity <= 0) {
            reason = $"quantity {mutation.Quantity} must be positive";

            return false;
        }

        if (WorldDefinitionRows.FindStateRow(
            rows: current.State,
            name: mutation.ItemRow
        ) is not { } itemRow) {
            reason = $"no state row named '{mutation.ItemRow}'";

            return false;
        }

        if (WorldDefinitionRows.FindStateRow(
            rows: current.State,
            name: mutation.CurrencyRow
        ) is not { } currencyRow) {
            reason = $"no state row named '{mutation.CurrencyRow}'";

            return false;
        }

        if (
            (itemRow.Kind != CellKind.Int) ||
            (itemRow.Capacity is null)
        ) {
            reason = $"'{mutation.ItemRow}' is not a capacity-bounded int state row";

            return false;
        }

        if (
            (currencyRow.Kind != CellKind.Int) ||
            (currencyRow.Capacity is null)
        ) {
            reason = $"'{mutation.CurrencyRow}' is not a capacity-bounded int state row";

            return false;
        }

        if (mutation.Format == WorldMarketFormat.English) {
            if (mutation.StartPrice <= 0) {
                reason = "startPrice must be positive for an English listing";

                return false;
            }
        } else {
            if (
                (mutation.BuyoutPrice is not { } requiredBuyout) ||
                (requiredBuyout <= 0)
            ) {
                reason = "a buyout listing requires a positive buyoutPrice";

                return false;
            }

            // startPrice is unused by buyout (market.list's own help text says so); refused rather than silently
            // carried, the same door-not-type instinct WorldDefinitionValidator.ValidateMarket's Buyout arm applies
            // to currentBid/currentBidder — this is the immediate, per-field refusal, before whole-document
            // revalidation would catch the same thing with a less specific reason.
            if (mutation.StartPrice != 0) {
                reason = "startPrice is unused by buyout — pass 0";

                return false;
            }
        }

        if (
            (mutation.BuyoutPrice is { } declaredBuyout) &&
            (declaredBuyout <= 0)
        ) {
            reason = "buyoutPrice must be positive";

            return false;
        }

        if (current.SimulationRateHz <= 0) {
            reason = "a market listing needs a tick-mapped duration, refused in a rate-0 world";

            return false;
        }

        var sellerBalance = ReadMarketCellValue(
            definition: current,
            row: mutation.ItemRow,
            key: sellerKey,
            tick: tick
        );

        if (sellerBalance < mutation.Quantity) {
            reason = $"seller holds {sellerBalance} of '{mutation.ItemRow}', short of the {mutation.Quantity} listed";

            return false;
        }

        var durationTicks = WorldSimulationTickConversion.DurationTicks(
            seconds: mutation.DurationSeconds,
            ratePerSecond: ((uint)current.SimulationRateHz)
        );
        var deadlineTick = unchecked((((long)tick) + ((long)durationTicks)));

        var state = WriteMarketCell(
            rows: current.State,
            rowName: mutation.ItemRow,
            key: sellerKey,
            value: (sellerBalance - mutation.Quantity)
        );

        var listing = new WorldMarketListing(
            Id: market.NextListingId,
            Seller: mutation.Seller,
            ItemRow: mutation.ItemRow,
            Quantity: mutation.Quantity,
            CurrencyRow: mutation.CurrencyRow,
            Format: mutation.Format,
            StartPrice: mutation.StartPrice,
            BuyoutPrice: mutation.BuyoutPrice,
            DeadlineTick: deadlineTick
        );

        var newMarket = (market with {
            Listings = Upsert(
            list: (market.Listings ?? []),
            item: listing,
            keyOf: static (WorldMarketListing l) => l.Id
        ),
            NextListingId = (market.NextListingId + 1),
        });

        candidate = (current.WithWorldState(rows: state) with { Market = newMarket });

        return true;
    }
    // market.bid — escrows Amount out of the bidder's own currency cell, refunding any standing bidder in the SAME
    // candidate. English format only. A standing bidder raising their OWN bid is netted against their own standing
    // escrow (one read, one write, delta-charged) rather than charged the full new amount and then "refunded" the
    // old one through a second read of the very cell this compose pass just wrote — that second read would see the
    // cell's pre-rebase Advance/EpochTick (RebaseCellTraits runs AFTER TryCompose) and re-apply the elapsed
    // accrual TryRead already folded into the first read, on top of a base that already carries it.
    private static bool TryComposePlaceMarketBid(WorldDefinition current, WorldMutation.PlaceMarketBid mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        var market = (current.Market ?? WorldMarketSection.Empty);

        if (FindMarketListing(
            listings: (market.Listings ?? []),
            id: mutation.ListingId
        ) is not { } listing) {
            reason = $"no listing #{mutation.ListingId}";

            return false;
        }

        if (listing.Status != WorldMarketListingStatus.Active) {
            reason = $"listing #{mutation.ListingId} is {listing.Status}, not active";

            return false;
        }

        if (unchecked((long)tick) >= listing.DeadlineTick) {
            reason = $"listing #{mutation.ListingId} has reached its deadline";

            return false;
        }

        if (listing.Format != WorldMarketFormat.English) {
            reason = $"listing #{mutation.ListingId} is {listing.Format}, which takes no incremental bids";

            return false;
        }

        if (!TryPlayerCellKey(
            principal: mutation.Bidder,
            key: out var bidderKey
        )) {
            reason = $"bidder {mutation.Bidder.Describe()} must be a seat or peer";

            return false;
        }

        if (!TryAuthorizeMarketParty(
            actingPrincipal: mutation.Principal,
            party: mutation.Bidder,
            reason: out reason
        )) {
            return false;
        }

        if (mutation.Bidder == listing.Seller) {
            reason = "the seller may not bid on their own listing";

            return false;
        }

        // long.MaxValue is a legal carried balance/bid, but it has no representable successor. Refuse explicitly:
        // adding one would wrap the minimum negative, admit a lower bid, and make a self-bid's net charge negative.
        if (listing.CurrentBid == long.MaxValue) {
            reason = $"listing #{mutation.ListingId} already carries the maximum representable bid and cannot be raised";

            return false;
        }

        var minBid = ((listing.CurrentBid > 0)
            ? (listing.CurrentBid + 1)
            : listing.StartPrice
        );

        if (mutation.Amount < minBid) {
            reason = $"bid {mutation.Amount} does not meet the minimum {minBid}";

            return false;
        }

        var isSelfRaise = (mutation.Bidder == listing.CurrentBidder);
        var netCharge = (isSelfRaise
            ? (mutation.Amount - listing.CurrentBid)
            : mutation.Amount
        );

        var bidderBalance = ReadMarketCellValue(
            definition: current,
            row: listing.CurrencyRow,
            key: bidderKey,
            tick: tick
        );

        if (bidderBalance < netCharge) {
            reason = (isSelfRaise
                ? $"bidder holds {bidderBalance} of '{listing.CurrencyRow}', short of the {netCharge} additional needed to raise from {listing.CurrentBid} to {mutation.Amount}"
                : $"bidder holds {bidderBalance} of '{listing.CurrencyRow}', short of the {mutation.Amount} bid"
            );

            return false;
        }

        var state = WriteMarketCell(
            rows: current.State,
            rowName: listing.CurrencyRow,
            key: bidderKey,
            value: (bidderBalance - netCharge)
        );

        if (
            !isSelfRaise &&
            (listing.CurrentBidder is { } previousBidder) &&
            TryPlayerCellKey(
            key: out var previousKey,
            principal: previousBidder
        )
        ) {
            var previousBalance = ReadMarketCellValue(
                definition: current.WithWorldState(rows: state),
                row: listing.CurrencyRow,
                key: previousKey,
                tick: tick
            );

            state = WriteMarketCell(
                rows: state,
                rowName: listing.CurrencyRow,
                key: previousKey,
                value: (previousBalance + listing.CurrentBid)
            );
        }

        var updatedListing = (listing with { CurrentBid = mutation.Amount, CurrentBidder = mutation.Bidder });
        var newMarket = (market with {
            Listings = Upsert(
            list: (market.Listings ?? []),
            item: updatedListing,
            keyOf: static (WorldMarketListing l) => l.Id
        ),
        });

        candidate = (current.WithWorldState(rows: state) with { Market = newMarket });

        return true;
    }
    // market retention sweep — removes every terminal (settled/cancelled/expired) row whose ResolvedTick lies at
    // least market.retentionSeconds (converted the same way a listing's own duration becomes its DeadlineTick)
    // behind the applying tick, in the same candidate. An active row is never touched, and NextListingId never
    // rewinds — a pruned id is retired, not reissued. World-only, fired by PruneExpiredMarketListings once at least
    // one row is eligible; refuses (a no-op) if it somehow fires with nothing eligible, rather than journaling an
    // empty "prune".
    private static bool TryComposePruneMarketListings(WorldDefinition current, WorldMutation.PruneMarketListings mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        if (mutation.Principal != WorldPrincipal.World) {
            reason = "only the engine's own retention sweep may prune market listings";

            return false;
        }

        if (current.Market is not { } market) {
            reason = "this world authors no market section";

            return false;
        }

        if (current.SimulationRateHz <= 0) {
            reason = "market retention needs a tick-mapped window, refused in a rate-0 world";

            return false;
        }

        var retentionTicks = unchecked((long)WorldSimulationTickConversion.DurationTicks(
            seconds: market.RetentionSeconds,
            ratePerSecond: ((uint)current.SimulationRateHz)
        ));
        var listings = (market.Listings ?? []);
        List<WorldMarketListing>? kept = null;

        for (var index = 0; (index < listings.Count); index++) {
            var listing = listings[index];
            var eligible = ((listing.Status != WorldMarketListingStatus.Active)
                && (listing.ResolvedTick is { } resolvedTick)
                && (unchecked((long)tick) >= unchecked((resolvedTick + retentionTicks))));

            if (eligible) {
                kept ??= new List<WorldMarketListing>(collection: listings.Take(count: index));

                continue;
            }

            kept?.Add(item: listing);
        }

        if (kept is null) {
            reason = "no terminal listing has yet reached market.retentionSeconds";

            return false;
        }

        candidate = (current with { Market = (market with { Listings = kept }) });

        return true;
    }
    // The engine's own deadline sweep, fired by Server.WorldServer's per-tick market pass (the SAME shape as its own
    // ReclaimExpiredEscrows) under WorldPrincipal.World — never reachable from a player submission. A standing
    // English bid settles (pays the seller net of fee, credits the winner's item cell); no bid at all expires
    // (returns the escrowed item to the seller).
    private static bool TryComposeSettleMarketListing(WorldDefinition current, WorldMutation.SettleMarketListing mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        if (mutation.Principal != WorldPrincipal.World) {
            reason = "only the engine's own timeout sweep may settle a listing";

            return false;
        }

        var market = (current.Market ?? WorldMarketSection.Empty);

        if (FindMarketListing(
            listings: (market.Listings ?? []),
            id: mutation.ListingId
        ) is not { } listing) {
            reason = $"no listing #{mutation.ListingId}";

            return false;
        }

        if (listing.Status != WorldMarketListingStatus.Active) {
            reason = $"listing #{mutation.ListingId} is {listing.Status}, not active";

            return false;
        }

        if (unchecked((long)tick) < listing.DeadlineTick) {
            reason = $"listing #{mutation.ListingId} has not yet reached its deadline";

            return false;
        }

        if (!TryPlayerCellKey(
            principal: listing.Seller,
            key: out var sellerKey
        )) {
            reason = "listing seller is not a seat or peer";

            return false;
        }

        if (
            (listing.CurrentBidder is { } winner) &&
            TryPlayerCellKey(
            key: out var winnerKey,
            principal: winner
        )
        ) {
            var fee = MarketFee(
                amount: listing.CurrentBid,
                feeBasisPoints: market.FeeBasisPoints
            );
            var sellerBalance = ReadMarketCellValue(
                definition: current,
                row: listing.CurrencyRow,
                key: sellerKey,
                tick: tick
            );
            var state = WriteMarketCell(
                rows: current.State,
                rowName: listing.CurrencyRow,
                key: sellerKey,
                value: (sellerBalance + (listing.CurrentBid - fee))
            );
            var winnerItemBalance = ReadMarketCellValue(
                definition: current.WithWorldState(rows: state),
                row: listing.ItemRow,
                key: winnerKey,
                tick: tick
            );

            state = WriteMarketCell(
                rows: state,
                rowName: listing.ItemRow,
                key: winnerKey,
                value: (winnerItemBalance + listing.Quantity)
            );

            var settled = (listing with { Status = WorldMarketListingStatus.Settled, ResolvedTick = unchecked((long)tick) });

            candidate = (current.WithWorldState(rows: state) with {
                Market = (market with {
                    Listings = Upsert(
                list: (market.Listings ?? []),
                item: settled,
                keyOf: static (WorldMarketListing l) => l.Id
            ),
                    FeeReserve = (market.FeeReserve + fee),
                }),
            });

            return true;
        }

        // No bid ever landed — expiry, not a sale: return the escrowed item.
        var sellerItemBalance = ReadMarketCellValue(
            definition: current,
            row: listing.ItemRow,
            key: sellerKey,
            tick: tick
        );
        var expiredState = WriteMarketCell(
            rows: current.State,
            rowName: listing.ItemRow,
            key: sellerKey,
            value: (sellerItemBalance + listing.Quantity)
        );
        var expired = (listing with { Status = WorldMarketListingStatus.Expired, ResolvedTick = unchecked((long)tick) });

        candidate = (current.WithWorldState(rows: expiredState) with {
            Market = (market with {
                Listings = Upsert(
            list: (market.Listings ?? []),
            item: expired,
            keyOf: static (WorldMarketListing l) => l.Id
        ),
            }),
        });

        return true;
    }
    // Writes a market fact cell's quantity/balance, preserving whatever Advance/Dynamics/Provenance the cell already
    // carried (a market move is a value write, never a re-mint) — the SAME base-value-write-preserves-trait rule
    // UpsertStateCell's own compose arm follows. Assumes `rowName` already resolved against `rows` (every caller
    // validates existence first); the row's declared envelope (Min/Max/NonNegative) is left to the whole-document
    // revalidation TryApplyMutation runs after compose, exactly like every other state write here.
    private static IReadOnlyList<WorldStateRow> WriteMarketCell(IReadOnlyList<WorldStateRow> rows, WorldCellName rowName, string key, long value) {
        var row = WorldDefinitionRows.FindStateRow(
            name: rowName,
            rows: rows
        )!;
        var cellKey = WorldCellName.Parse(candidate: key);
        var existingCell = WorldDefinitionRows.FindCell(
            cells: row.Cells,
            key: cellKey
        );
        var existingAdvance = existingCell?.Advance;
        var existingDynamics = existingCell?.Dynamics;
        var existingProvenance = existingCell?.Provenance;
        var cells = Upsert(
            list: (row.Cells ?? []),
            item: new WorldStateCell(
                Key: cellKey,
                Value: value,
                Advance: existingAdvance,
                Provenance: existingProvenance,
                Dynamics: existingDynamics
            ),
            keyOf: static (WorldStateCell cell) => cell.Key
        );

        return Upsert(
            list: rows,
            item: (row with { Cells = cells }),
            keyOf: static (WorldStateRow r) => r.Name
        );
    }

    // One (row, key) cell a market compose arm wrote through WriteMarketCell.
    private readonly record struct MarketCellTouch(WorldCellName Row, string Key);
}
