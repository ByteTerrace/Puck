namespace Puck.Commands;

/// <summary>Maps a logical player slot to the identity currently ACTING through it — the seam the snapshot mixer
/// stamps a <see cref="CommandPrincipal"/> from.</summary>
/// <remarks>The peer of <see cref="IInputSlotResolver"/>: that one answers which slot a device belongs to, this one
/// answers who that slot is. They are separate questions because a slot can be CLAIMED — an editor session, a replay
/// device, a network peer stand-in — so a mixer that synthesized <see cref="CommandPrincipal.Seat"/> from the slot
/// number would attribute a claimant's action to the seat it displaced. The host's roster is the only thing that
/// knows, so it answers here.</remarks>
public interface ICommandPrincipalResolver {
    /// <summary>The identity acting through <paramref name="slot"/> — its claimant when something claimed it, its own
    /// seat otherwise. Never <see cref="CommandPrincipalKind.Unspecified"/> for a slot the host admits.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <returns>The slot's acting identity.</returns>
    CommandPrincipal PrincipalOf(int slot);
}
