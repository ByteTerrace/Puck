using System.Globalization;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>The one definition of what reading a body's named action-state slot answers, shared by a world rule's
/// <see cref="WorldRuleFacts.BodyStatePrefix"/> read and a look's render lane: which slot a name resolves to, and the
/// number a stored slot presents.</summary>
/// <remarks>A slot is declared in <c>state.body</c> or <c>state.identity</c>; both lists form one register file, body
/// slots first, every name unique across the two. Every read answers a <see cref="CellKind.Fixed"/> number: a counter
/// its value, a timer its remaining time in seconds (whole engine ticks divided by
/// <see cref="FixedTickConversion.TicksPerSecond"/>, rounded down), so a rule or a lane compares a timer against the
/// seconds it was authored in.</remarks>
public static class WorldBodyStateReads {
    /// <summary>The reserved channel's name, without the prefix: <c>bodyState(each, fuel)</c> in <c>.puck</c>.</summary>
    public const string ChannelName = "bodyState";
    /// <summary>The prefix of the row name a presentation binding reads a body-state slot through:
    /// <c>$bodyState:&lt;slot&gt;</c>, keyed by the body's decimal index or <see cref="StateBinding.BodyKey"/>. A
    /// reserved prefix, so no authored row shares it.</summary>
    public const string RowPrefix = "$bodyState:";

    /// <summary>Returns the presentation row a body-state slot is read through.</summary>
    /// <param name="slot">The slot's name.</param>
    /// <returns>The row <see cref="RowPrefix"/> and the name spell, which a binding keys by the body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="slot"/> is <see langword="null"/>.</exception>
    public static string BindingRow(string slot) {
        ArgumentNullException.ThrowIfNull(argument: slot);

        return string.Concat(
            str0: RowPrefix,
            str1: slot
        );
    }
    /// <summary>Resolves a declared slot by name.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="name">The slot's name.</param>
    /// <param name="slot">The slot's index in the register file (body slots, then identity slots).</param>
    /// <param name="kind">Whether the slot is a counter or a timer.</param>
    /// <returns><see langword="true"/> when the definition declares the slot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="name"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryResolveSlot(WorldDefinition definition, string name, out int slot, out ActionStateKind kind) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: name);

        var bodyState = definition.BodyState;
        var identityState = definition.IdentityState;

        for (var index = 0; (index < (bodyState.Count + identityState.Count)); index++) {
            var declared = ((index < bodyState.Count)
                ? bodyState[index]
                : identityState[(index - bodyState.Count)]
            );

            if (string.Equals(
                a: declared.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                slot = index;
                kind = declared.Kind;

                return true;
            }
        }

        slot = -1;
        kind = default;

        return false;
    }
    /// <summary>Returns the number a stored slot presents, as raw <see cref="FixedQ4816"/> bits.</summary>
    /// <param name="kind">The slot's kind.</param>
    /// <param name="raw">The stored value: a counter's raw bits, or a timer's remaining engine ticks.</param>
    /// <returns>A counter's raw bits unchanged, or a timer's remaining seconds rounded to nearest with ties to even
    /// (<see cref="FixedTickConversion.SecondsOf"/>); a negative tick count presents as zero.</returns>
    public static long Presented(ActionStateKind kind, long raw) => ((kind == ActionStateKind.Counter)
        ? raw
        : FixedTickConversion.SecondsOf(
            ratePerSecond: FixedTickConversion.TicksPerSecond,
            ticks: ((ulong)Math.Max(
                val1: 0L,
                val2: raw
            ))
        ).Value
    );
    /// <summary>Reads a render lane's state operand as a body-state binding: <c>bodyState($body, slot)</c> reads the
    /// wearing body's slot, <c>bodyState(body, n, slot)</c> body <c>n</c>'s.</summary>
    /// <param name="name">The operand's name.</param>
    /// <param name="key">The operand's key, which a body-state read never carries.</param>
    /// <param name="binding">The presentation binding: the row <see cref="RowPrefix"/> and the slot name, keyed by the
    /// body.</param>
    /// <returns><see langword="true"/> when the operand is a well-formed body-state read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static bool TryBinding(StateChannelRef name, StateChannelRef? key, out StateBinding binding) {
        ArgumentNullException.ThrowIfNull(argument: name);

        binding = default;

        if (
            (key is not null) ||
            (name.Call is not { Channel: ChannelName } call)
        ) {
            return false;
        }

        var texts = call.Texts();
        string body;

        if (
            (texts.Length == 2) &&
            string.Equals(
                a: texts[0],
                b: StateBinding.BodyKey,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            body = StateBinding.BodyKey;
        } else if (
            (texts.Length == 3) &&
            string.Equals(
                a: texts[0],
                b: "body",
                comparisonType: StringComparison.Ordinal
            ) &&
            int.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var index,
                s: texts[1],
                style: NumberStyles.None
            )
        ) {
            body = index.ToString(provider: CultureInfo.InvariantCulture);
        } else {
            return false;
        }

        binding = new StateBinding(
            Key: body,
            Row: $"{RowPrefix}{texts[^1]}",
            Target: false
        );

        return true;
    }
}
