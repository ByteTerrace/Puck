using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Shared seat-scoped console-argument helpers: seat resolution and echo formatting, usable by any seat-scoped verb
/// without a hard-coded mode.
/// </summary>
internal static class SeatCommandArgs {
    /// <summary>Formats a seat-scoped command result for the transcript.</summary>
    /// <param name="slot">The zero-based player slot.</param>
    /// <param name="verb">The command name.</param>
    /// <param name="detail">The command result detail.</param>
    /// <returns>The formatted command result.</returns>
    internal static CommandResult Echo(int slot, string verb, string detail) =>
        new(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} {detail}]");
    /// <summary>Resolves the acting seat: a present trailing [seat] token (1..4) is authoritative; an absent one
    /// falls back to <paramref name="defaultSlot"/> when given, otherwise to <see cref="CommandContext.Slot"/> — the
    /// pressing device's seat for a bound chord act, and 0 for an unseated administrative stdin line, but the
    /// ACTUAL seat for a session opened via <c>console on &lt;player&gt;</c>
    /// (<see cref="TextCommandSource.CreateSeatSession"/>). Token presence is the discriminator, never
    /// <see cref="CommandContext.Parse"/>: the registry's Immediate fast path hands wire handlers a null Parse for
    /// typed lines too, so a Parse-null test would silently ignore a typed seat token.</summary>
    /// <param name="context">The invocation context.</param>
    /// <param name="args">The verb args.</param>
    /// <param name="at">The trailing seat token's index.</param>
    /// <param name="verb">The verb name for error text.</param>
    /// <param name="defaultSlot">The 0-based slot to resolve to when the token is absent, overriding the
    /// <see cref="CommandContext.Slot"/> fallback — pass <c>0</c> for a verb documented as "default 1" regardless of
    /// the acting session.</param>
    /// <returns>The resolved 0-based slot, or an error result on a malformed index (-1 slot).</returns>
    internal static (int Slot, CommandResult? Error) ResolveSlot(CommandContext context, in WireArgs args, int at, string verb, int? defaultSlot = null) {
        if (args.Count <= at) {
            return (Slot: (defaultSlot ?? context.Slot), Error: null);
        }

        if (!WorldArgs.TryParseIndex(
            args: args,
            at: at,
            fallback: null,
            max: PlayerRoster.MaxSlots,
            min: 1,
            value: out var seat
        )) {
            return (Slot: -1, Error: CommandResult.Error(output: $"[{verb}: seat must be an integer 1..{PlayerRoster.MaxSlots}]"));
        }

        return (Slot: PlayerRoster.SlotFromDisplay(number: seat), Error: null);
    }
    /// <summary>Resolves the acting seat exactly like <see cref="ResolveSlot"/>, additionally requiring it to be
    /// JOINED — the gate a verb that DRIVES or MUTATES a live seat needs, in the ONE wording every call site shares.
    /// A pure read-back names no joined requirement of its own (see <see cref="ResolveSlot"/>): it describes
    /// whatever slot it is asked about, joined or not.</summary>
    /// <param name="roster">The player roster.</param>
    /// <param name="context">The invocation context.</param>
    /// <param name="args">The verb args.</param>
    /// <param name="at">The trailing seat token's index.</param>
    /// <param name="verb">The verb name for error text.</param>
    /// <param name="defaultSlot">See <see cref="ResolveSlot"/>.</param>
    /// <returns>The resolved 0-based slot, or an error result on a malformed index or an unjoined seat.</returns>
    internal static (int Slot, CommandResult? Error) ResolveJoinedSeat(PlayerRoster roster, CommandContext context, in WireArgs args, int at, string verb, int? defaultSlot = null) {
        var (slot, error) = ResolveSlot(
            args: in args,
            at: at,
            context: context,
            defaultSlot: defaultSlot,
            verb: verb
        );

        if (error is not null) {
            return (Slot: slot, Error: error);
        }

        return (roster.IsJoined(slot: slot)
            ? (Slot: slot, Error: null)
            : (Slot: slot, Error: CommandResult.Error(output: $"[{verb}: player {PlayerRoster.DisplayNumber(slot: slot)} is not joined]"))
        );
    }
}
