namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Each token is selected afresh from what remains, so a random transfer samples once per token and a
    // positional transfer walks the pile from its chosen end. Onto its own zone a transfer is a reorder, because
    // the token never leaves the row and the row's room never changes.
    private static bool TryTransfer(in ArenaTransformContext context, ArenaTransform.Transfer transfer, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var arena = context.Arena;
        var fromOrdinal = binding.FromOr(own: transfer.FromRowOrdinal);
        var toOrdinal = binding.ToOr(own: transfer.ToRowOrdinal);

        if (
            !TryMemberRow(
            code: TransformRefusal.TransferEndsMismatched,
            context: in context,
            layout: out var from,
            refusal: out refusal,
            rowOrdinal: fromOrdinal,
            verb: "transfer 'from'"
        ) ||
            !TryMemberRow(
            code: TransformRefusal.TransferEndsMismatched,
            context: in context,
            layout: out var to,
            refusal: out refusal,
            rowOrdinal: toOrdinal,
            verb: "transfer 'to'"
        )
        ) {
            return false;
        }
        if (
            !from.IsOrdered ||
            !to.IsOrdered ||
            (from.DomainOrdinal < 0) ||
            (from.DomainOrdinal != to.DomainOrdinal) ||
            // Named rather than Enum.IsDefined, which reads the enum's reflected member table and re-reads it after
            // every collection; a selector the resolver's own Enum.IsDefined admitted is one of these five.
            (transfer.Selector is not (ZoneSelector.Key or ZoneSelector.First or ZoneSelector.Last or ZoneSelector.Random or ZoneSelector.Slice))
        ) {
            return Refuse(
                code: TransformRefusal.TransferEndsMismatched,
                reason: $"transfer requires zones in one token domain and a defined selector: '{RowName(
                    context: in context,
                    rowOrdinal: fromOrdinal
                )}' is {from.Shape} over domain ordinal {from.DomainOrdinal} and '{RowName(
                    context: in context,
                    rowOrdinal: toOrdinal
                )}' is {to.Shape} over domain ordinal {to.DomainOrdinal}",
                refusal: out refusal
            );
        }

        var key = binding.KeyOr(own: transfer.Key);
        var keyed = (transfer.Selector is ZoneSelector.Key or ZoneSelector.Slice);

        if (
            (keyed != key.IsValid) ||
            ((transfer.Selector == ZoneSelector.Random) != (transfer.DrawRowOrdinal >= 0))
        ) {
            return Refuse(
                code: TransformRefusal.TransferSelectorArguments,
                reason: "key and slice selection require only key; random selection requires only draw",
                refusal: out refusal
            );
        }
        if (
            (transfer.Count < 1) ||
            (transfer.Count > StateTransferCapacity.MaxTransferCount) ||
            (keyed && (transfer.Count != 1))
        ) {
            return Refuse(
                code: TransformRefusal.TransferSelectorArguments,
                reason: $"transfer count must be 1..{StateTransferCapacity.MaxTransferCount}, and exactly 1 for a key or slice selection",
                refusal: out refusal
            );
        }
        if (transfer.Selector == ZoneSelector.Slice) {
            return TrySlice(
                context: in context,
                fromOrdinal: fromOrdinal,
                insertFirst: transfer.InsertFirst,
                key: key,
                moved: out moved,
                refusal: out refusal,
                toOrdinal: toOrdinal
            );
        }

        var count = arena.CellCount(rowOrdinal: fromOrdinal);

        if (count < transfer.Count) {
            return Refuse(
                code: TransformRefusal.TransferSourceShort,
                reason: ((count == 0)
                ? "source zone is empty"
                : $"source zone holds {count} tokens, fewer than the {transfer.Count} to transfer"),
                refusal: out refusal
            );
        }
        if (
            (fromOrdinal != toOrdinal) &&
            ((arena.CellCount(rowOrdinal: toOrdinal) + transfer.Count) > to.CellCapacity)
        ) {
            return Refuse(
                code: TransformRefusal.TransferDestinationFull,
                reason: "destination zone is full",
                refusal: out refusal
            );
        }

        StateGenerator generator = default!;
        Draw draw = default!;

        if (
            (transfer.Selector == ZoneSelector.Random) &&
            !TryDrawSite(
            code: TransformRefusal.TransferDrawSite,
            context: in context,
            draw: out draw,
            generator: out generator,
            refusal: out refusal,
            rowOrdinal: transfer.DrawRowOrdinal,
            verb: "random transfer"
        )
        ) {
            return false;
        }

        var sample = 0L;

        for (var token = 0; (token < transfer.Count); token++) {
            var remaining = arena.CellCount(rowOrdinal: fromOrdinal);
            var selected = transfer.Selector switch {
                ZoneSelector.First => 0,
                ZoneSelector.Last => (remaining - 1),
                ZoneSelector.Key => (arena.TryCellSlot(
                key: key,
                rowOrdinal: fromOrdinal,
                slot: out var slot
            )
                    ? (slot - from.CellStart)
                    : -1
                ),
                _ => 0,
            };

            if (transfer.Selector == ZoneSelector.Random) {
                if (!TrySample(
                    code: TransformRefusal.TransferDrawSite,
                    context: in context,
                    draw: in draw,
                    generator: generator,
                    refusal: out refusal,
                    rowOrdinal: transfer.DrawRowOrdinal,
                    sample: out sample
                )) {
                    return false;
                }

                selected = Select(
                    count: remaining,
                    sample: sample
                );
            }
            if (
                (selected < 0) ||
                !arena.TryKeyAt(
                key: out var selectedKey,
                position: selected,
                rowOrdinal: fromOrdinal
            )
            ) {
                return Refuse(
                    code: TransformRefusal.TransferTokenAbsent,
                    reason: "source zone does not contain the selected token",
                    refusal: out refusal
                );
            }
            if (fromOrdinal == toOrdinal) {
                if (!TryMoveWithin(
                    context: in context,
                    count: remaining,
                    insertFirst: transfer.InsertFirst,
                    position: selected,
                    refusal: out refusal,
                    rowOrdinal: fromOrdinal
                )) {
                    return false;
                }
            } else if (!arena.TryTransfer(
                fromOrdinal: fromOrdinal,
                insertFirst: transfer.InsertFirst,
                key: selectedKey,
                reason: out var reason,
                toOrdinal: toOrdinal
            )) {
                return Refuse(
                    code: TransformRefusal.TransferRejected,
                    reason: reason,
                    refusal: out refusal
                );
            }

            moved = true;
        }
        if (
            (transfer.Selector == ZoneSelector.Random) &&
            !TryRecordSample(
            code: TransformRefusal.TransferDrawSite,
            context: in context,
            refusal: out refusal,
            rowOrdinal: transfer.DrawRowOrdinal,
            sample: sample
        )
        ) {
            return false;
        }

        return Applied(refusal: out refusal);
    }
    // The keyed token and every token after it move as one run, in order: the cascade a solitaire column hands over
    // from a card to its top. Onto the same zone the run rotates to the other end.
    private static bool TrySlice(in ArenaTransformContext context, int fromOrdinal, int toOrdinal, CellKey key, bool insertFirst, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var arena = context.Arena;
        ref readonly var from = ref arena.Layout[fromOrdinal];

        if (!arena.TryCellSlot(
            key: key,
            rowOrdinal: fromOrdinal,
            slot: out var slot
        )) {
            return Refuse(
                code: TransformRefusal.TransferTokenAbsent,
                reason: "source zone does not contain the selected token",
                refusal: out refusal
            );
        }

        var count = arena.CellCount(rowOrdinal: fromOrdinal);
        var start = (slot - from.CellStart);
        var run = (count - start);

        if (fromOrdinal == toOrdinal) {
            if (!insertFirst) {
                return Applied(refusal: out refusal);
            }

            using var orderLease = context.Arena.Scratch.Rent<int>(length: count);

            var order = orderLease.Span;
            for (var position = 0; (position < count); position++) {
                order[position] = ((position < run)
                    ? (start + position)
                    : (position - run)
                );
            }

            return TryReorder(
                code: TransformRefusal.TransferRejected,
                context: in context,
                moved: out moved,
                order: order,
                refusal: out refusal,
                rowOrdinal: fromOrdinal
            );
        }
        if ((arena.CellCount(rowOrdinal: toOrdinal) + run) > arena.Layout[toOrdinal].CellCapacity) {
            return Refuse(
                code: TransformRefusal.TransferDestinationFull,
                reason: "destination zone is full",
                refusal: out refusal
            );
        }

        // The run keeps its order, so each token enters at the destination's tail, or the run is walked backwards
        // onto its head.
        for (var token = 0; (token < run); token++) {
            var position = (insertFirst
                ? ((count - 1) - token)
                : start
            );

            if (!arena.TryKeyAt(
                key: out var moving,
                position: position,
                rowOrdinal: fromOrdinal
            )) {
                return Refuse(
                    code: TransformRefusal.TransferTokenAbsent,
                    reason: "source zone does not contain the selected token",
                    refusal: out refusal
                );
            }
            if (!arena.TryTransfer(
                fromOrdinal: fromOrdinal,
                insertFirst: insertFirst,
                key: moving,
                reason: out var reason,
                toOrdinal: toOrdinal
            )) {
                return Refuse(
                    code: TransformRefusal.TransferRejected,
                    reason: reason,
                    refusal: out refusal
                );
            }

            moved = true;
        }

        return Applied(refusal: out refusal);
    }
    // One token taken out of a pile and put back at one of its ends: the permutation that leaves every other
    // token's relative order alone.
    private static bool TryMoveWithin(in ArenaTransformContext context, int rowOrdinal, int position, int count, bool insertFirst, out EffectRefusal refusal) {
        if (count < 2) {
            return Applied(refusal: out refusal);
        }

        using var orderLease = context.Arena.Scratch.Rent<int>(length: count);

        var order = orderLease.Span;
        for (var slot = 0; (slot < count); slot++) {
            order[slot] = (insertFirst
                ? ((slot == 0)
                    ? position
                    : ((slot <= position)
                        ? (slot - 1)
                        : slot
                    )
                )
                : ((slot == (count - 1))
                    ? position
                    : ((slot < position)
                        ? slot
                        : (slot + 1)
                    )
                )
            );
        }

        return TryReorder(
            code: TransformRefusal.TransferRejected,
            context: in context,
            moved: out _,
            order: order,
            refusal: out refusal,
            rowOrdinal: rowOrdinal
        );
    }
}
