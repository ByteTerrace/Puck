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
    /// <summary>Resolves the acting seat: a present trailing [seat] token (1..4) is authoritative; an absent one falls
    /// back to the invocation's slot — the pressing device's seat for a bound chord act, and the text path's default
    /// seat 1 (<see cref="CommandContext.Slot"/> is 0 there by contract). Token presence is the discriminator, never
    /// <see cref="CommandContext.Parse"/>: the registry's Immediate fast path hands wire handlers a null
    /// Parse for typed lines too, so a Parse-null test would silently ignore a typed seat token.</summary>
    /// <param name="context">The invocation context.</param>
    /// <param name="args">The verb args.</param>
    /// <param name="at">The trailing seat token's index.</param>
    /// <param name="verb">The verb name for error text.</param>
    /// <returns>The resolved 0-based slot, or an error result on a malformed index (-1 slot).</returns>
    internal static (int Slot, CommandResult? Error) ResolveSlot(CommandContext context, in WireArgs args, int at, string verb) {
        if (args.Count <= at) {
            return (Slot: context.Slot, Error: null);
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
}
