namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The arena's ring door clears the slot the cursor reuses before storing, so a ring's contents are decided by
    // what has been pushed into it and never by what the overwritten slot carried.
    private static bool TryPush(in ArenaTransformContext context, ArenaTransform.Push push, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        moved = false;

        if (
            (((uint)push.RowOrdinal) >= ((uint)context.Arena.Layout.RowCount)) ||
            (context.Arena.Layout[push.RowOrdinal].Shape != RowShape.Ring)
        ) {
            return Refuse(
                code: TransformRefusal.PushRowShape,
                reason: "push requires a history row",
                refusal: out refusal
            );
        }
        // A literal was converted to the ring's raw encoding once, at compile time; a live source is evaluated per
        // firing and arrives in the binding, so nothing reads a row or cell name here.
        if (
            push.Bound &&
            !binding.BindsValue
        ) {
            return Refuse(
                code: TransformRefusal.PushValueUnbound,
                reason: $"push reads its value from a live source, and this application of '{RowName(
                    context: in context,
                    rowOrdinal: push.RowOrdinal
                )}' resolved none",
                refusal: out refusal
            );
        }
        if (!context.Arena.TryPush(
            reason: out var reason,
            rowOrdinal: push.RowOrdinal,
            value: binding.ValueOr(own: push.Value)
        )) {
            return Refuse(
                code: TransformRefusal.PushValueInadmissible,
                reason: reason,
                refusal: out refusal
            );
        }

        // A push always moves the ring's cursor, even when the value repeats the slot it overwrote.
        moved = true;

        return Applied(refusal: out refusal);
    }
}
