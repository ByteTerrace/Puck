using System.Numerics;
using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>The channel ordinals a contributor's Drive row may reach — an untrusted (pooled) contributor's manifest,
/// or a document-mounted (trusted-by-authorship) addon's own declaration of which channels its world logic
/// touches.</summary>
/// <param name="Bits">The raw 64-bit lane, one bit per reachable channel ordinal.</param>
public readonly record struct ChannelReachMask(ulong Bits) {
    /// <summary>Gets every declared ordinal reached — a route's default mask (document-authored "reach nothing narrower
    /// than everything" baseline; see <c>Server.WorldEngagement</c>'s route channel mask).</summary>
    public static ChannelReachMask All { get; } = new(Bits: ((1UL << ChannelLimits.MaxChannels) - 1UL));
    /// <summary>Gets whether this mask reaches no channel at all.</summary>
    public bool IsEmpty => (Bits == 0UL);

    /// <summary>Determines whether <paramref name="ordinal"/> is reached.</summary>
    /// <param name="ordinal">The channel ordinal.</param>
    /// <returns><see langword="true"/> when the ordinal's bit is set.</returns>
    public bool Contains(int ordinal) => ((Bits & (1UL << ordinal)) != 0UL);
    /// <summary>Returns the pooled-fold narrowing step — <see cref="MeetMask64"/>'s bitwise-AND semilattice over which
    /// ordinals BOTH this reach and the occupying seat's authored consent admit. Only a genuinely untrusted (pooled)
    /// contributor's term narrows this way; a trusted-by-authorship addon's own Reach gates alone, with no seat
    /// consent to meet against (see <c>Puck.World.Server.WorldServer.StageContribution</c>).</summary>
    /// <param name="consent">The occupying seat's authored consent mask.</param>
    /// <returns>The channels both masks admit.</returns>
    public ChannelHeldMask Meet(ChannelConsentMask consent) =>
        new(Bits: MeetMask64.Meet(
            left: new MeetMask64(Bits: Bits),
            right: new MeetMask64(Bits: consent.Bits)
        ).Bits);
    /// <summary>Returns the mask with every ordinal <paramref name="declared"/> admits removed.</summary>
    /// <param name="declared">The ordinals to remove.</param>
    /// <returns>The narrowed mask.</returns>
    public ChannelReachMask Without(ChannelDeclaredMask declared) => new(Bits: Bits & ~declared.Bits);
}
/// <summary>The channel ordinals a mounted guest declares.</summary>
/// <param name="Bits">The raw 64-bit lane, one bit per declared channel ordinal.</param>
public readonly record struct ChannelDeclaredMask(ulong Bits) {
    /// <summary>Returns the mask with <paramref name="ordinal"/> additionally declared.</summary>
    /// <param name="ordinal">The channel ordinal to add.</param>
    /// <returns>The widened mask.</returns>
    public ChannelDeclaredMask With(int ordinal) => new(Bits: Bits | (1UL << ordinal));
}
/// <summary>The channel ordinals admitted from a contribution into this tick's fold.</summary>
/// <param name="Bits">The raw 64-bit lane, one bit per admitted channel ordinal.</param>
public readonly record struct ChannelHeldMask(ulong Bits) {
    /// <summary>Gets whether this mask admits no channel at all.</summary>
    public bool IsEmpty => (Bits == 0UL);

    /// <summary>Determines whether <paramref name="ordinal"/> is admitted.</summary>
    /// <param name="ordinal">The channel ordinal.</param>
    /// <returns><see langword="true"/> when the ordinal's bit is set.</returns>
    public bool Contains(int ordinal) => ((Bits & (1UL << ordinal)) != 0UL);
    /// <summary>Returns the union with <paramref name="other"/> — the ordinals EITHER mask admits.</summary>
    /// <param name="other">The mask to union with.</param>
    /// <returns>The union.</returns>
    public ChannelHeldMask Union(ChannelHeldMask other) => new(Bits: Bits | other.Bits);
    /// <summary>Returns the mask with <paramref name="ordinal"/> additionally admitted.</summary>
    /// <param name="ordinal">The channel ordinal to add.</param>
    /// <returns>The widened mask.</returns>
    public ChannelHeldMask With(int ordinal) => new(Bits: Bits | (1UL << ordinal));
}
/// <summary>The channel ordinals for which the occupying seat authored a positive pool ceiling.</summary>
/// <param name="Bits">The raw 64-bit lane, one bit per consented channel ordinal.</param>
public readonly record struct ChannelConsentMask(ulong Bits) {
    /// <summary>Gets whether this mask consents to no channel at all.</summary>
    public bool IsEmpty => (Bits == 0UL);

    /// <summary>Determines whether <paramref name="ordinal"/> is consented to.</summary>
    /// <param name="ordinal">The channel ordinal.</param>
    /// <returns><see langword="true"/> when the ordinal's bit is set.</returns>
    public bool Contains(int ordinal) => ((Bits & (1UL << ordinal)) != 0UL);
}

[System.Runtime.CompilerServices.InlineArray(ChannelLimits.MaxChannels)]
internal struct ChannelCeilingValues {
    private long m_element0;
}

/// <summary>The occupying seat's per-ordinal pool ceilings and their inseparable positive-value support.</summary>
public readonly struct ChannelCeilings {
    private readonly ChannelCeilingValues m_values;

    private ChannelCeilings(ChannelCeilingValues values, ChannelConsentMask support) {
        m_values = values;
        Support = support;
    }

    /// <summary>Gets the channel ordinals for which a ceiling has been authored.</summary>
    public ChannelConsentMask Support { get; }

    /// <summary>Gets the authored ceiling for <paramref name="ordinal"/>, or 0 when unauthored.</summary>
    /// <param name="ordinal">The channel ordinal.</param>
    public long this[int ordinal] => m_values[ordinal];

    /// <summary>Returns the ceilings with <paramref name="ceiling"/> authored for every ordinal
    /// <paramref name="channels"/> admits.</summary>
    /// <param name="channels">The ordinals to author the ceiling for.</param>
    /// <param name="ceiling">The positive ceiling value.</param>
    /// <returns>The widened ceilings.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ceiling"/> is less than or equal to
    /// 0.</exception>
    public ChannelCeilings WithCeiling(ChannelConsentMask channels, long ceiling) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            value: ceiling,
            other: 0L
        );

        var values = m_values;
        var validBits = channels.Bits & ((1UL << ChannelLimits.MaxChannels) - 1UL);
        var remaining = validBits;

        while (remaining != 0UL) {
            var ordinal = BitOperations.TrailingZeroCount(value: remaining);

            values[ordinal] = ceiling;
            remaining &= (remaining - 1UL);
        }

        return new ChannelCeilings(
            values: values,
            support: new ChannelConsentMask(Bits: Support.Bits | validBits)
        );
    }
}
