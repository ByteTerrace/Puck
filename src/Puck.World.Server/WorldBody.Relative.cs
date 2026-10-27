using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    private WorldChannelTable m_channels = WorldChannelTable.Empty;
    private long[] m_relativeRemainders = [];

    private PlayerIntent m_channelReadScalar;

    /// <summary>The body's current compiled input storage; addresses have meaning only in this mapping.</summary>
    public WorldChannelTable Channels => m_channels;

    /// <summary>Reads the step's scalar projection. Relative values round once with conserved fractional residue.</summary>
    public FixedQ4816 ReadChannelScalar(int ordinal) => m_channelReadScalar[ordinal];

    private void SetChannelTable(WorldChannelTable? channels) {
        var next = (channels ?? WorldChannelTable.Empty);

        if (ReferenceEquals(objA: m_channels, objB: next)) { return; }
        var sameLayout = (next.RelativeMask == m_channels.RelativeMask);
        var remaining = next.RelativeMask;

        while (sameLayout && (remaining != 0)) {
            var ordinal = BitOperations.TrailingZeroCount(value: remaining);

            remaining &= (remaining - 1);
            sameLayout = ((next.Name(ordinal: ordinal) == m_channels.Name(ordinal: ordinal)) && (next.RelativeUnit(ordinal: ordinal) == m_channels.RelativeUnit(ordinal: ordinal)));
        }
        m_channels = next;
        if (!sameLayout) { m_relativeRemainders = ((next.RelativeMask == 0) ? [] : new long[ChannelLimits.MaxAxes]); }
    }
    private PlayerIntent WithoutRelative(PlayerIntent intent) {
        var remaining = m_channels.RelativeMask;

        while (remaining != 0) {
            var ordinal = BitOperations.TrailingZeroCount(value: remaining);

            remaining &= (remaining - 1);
            intent = intent.WithRelative(ordinal: ordinal, value: default);
        }
        return intent;
    }
    private PlayerIntent ProjectRelative(PlayerIntent intent) {
        var remaining = m_channels.RelativeMask;

        while (remaining != 0) {
            var ordinal = BitOperations.TrailingZeroCount(value: remaining);

            remaining &= (remaining - 1);
            var delta = m_channels.ClampRelative(ordinal: ordinal, raw: intent.Channels.Relative(ordinal: ordinal).Value);
            var raw = checked((delta.Value + m_relativeRemainders[ordinal]));
            var projected = FixedQ3232.FromRawBits(value: raw).ToFixedQ4816();

            m_relativeRemainders[ordinal] = (raw - (projected.Value << 16));
            intent = intent.WithChannel(ordinal: ordinal, value: projected);
        }
        return intent;
    }
}
