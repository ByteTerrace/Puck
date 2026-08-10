using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The World-side half of the addon ABI's wire vocabularies: the <see cref="GrantRule"/> → <see cref="AddonVerdict"/>
/// mapping and the capability mask-bit mapping. These live here, not in the Simulation adapter, because one side of
/// each mapping is a <c>Puck.World</c> type the adapter must not reference. The input channel vocabulary is not
/// pinned here at all: what a guest may declare is whatever the world document's channels section declares,
/// resolved beside this in <see cref="WorldAddonChannelResolver"/>.
/// </summary>
internal static class WorldAddonWire {
    /// <summary>Maps a decided <see cref="GrantVerdict"/> rule onto its pinned wire value. Total over
    /// <see cref="GrantRule"/> — a new rule member fails loudly here rather than crossing as garbage.</summary>
    /// <param name="rule">The decided rule.</param>
    /// <returns>The pinned wire verdict.</returns>
    public static AddonVerdict FromRule(GrantRule rule) {
        return rule switch {
            GrantRule.ConcreteHold => AddonVerdict.HeldConcrete,
            GrantRule.WildcardHold => AddonVerdict.HeldWildcard,
            GrantRule.ReserverMatch => AddonVerdict.HeldAsReserver,
            GrantRule.NoHold => AddonVerdict.NoHold,
            GrantRule.BeatenByReserver => AddonVerdict.BeatenByReserver,
            // Both fallbacks reach an addon principal through a document-authored relationship rather than a row of
            // its own (an addon CAN be a group member per WorldDefinitionValidator.IsLegitimateGroupMember, and CAN
            // own a group as an OwnershipOwnerKind.Principal), so both are reachable from this door and neither has
            // a wire value of its own — mapped onto HeldWildcard, the closest existing shape ("allowed, not via a
            // concrete row of its own"), rather than left to throw.
            GrantRule.GroupHold => AddonVerdict.HeldWildcard,
            GrantRule.OwnershipHold => AddonVerdict.HeldWildcard,
            // DriveGated never reaches here: Allows() never produces it (composition-core's Seam A is scoped to
            // WorldServer.ApplyIntentSubmission and world.why alone — see GrantRule.DriveGated's own remarks), and
            // this mapping is fed exclusively from Allows() verdicts.
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(rule), actualValue: rule, message: "unmapped grant rule — the wire mapping must be extended deliberately, never defaulted"),
        };
    }

    /// <summary>Maps a single-bit ask capability mask onto the engine capability it requests. The pump already
    /// verified exactly one defined bit is set.</summary>
    /// <param name="mask">The single-bit mask (a <see cref="AddonCapabilityMask"/> value).</param>
    /// <param name="capability">The engine capability, when the bit maps.</param>
    /// <returns><see langword="true"/> when the bit maps to an engine capability.</returns>
    public static bool TryCapability(ulong mask, out WorldCapability capability) {
        switch (mask) {
            case AddonCapabilityMask.Drive:
                capability = WorldCapability.Drive;
                return true;
            case AddonCapabilityMask.Observe:
                capability = WorldCapability.Observe;
                return true;
            // AddonCapabilityMask.Reserved (bit 2, formerly Present) maps to no capability — the reserved hole
            // falls through to the default refusal below, same as any other undefined mask value.
            case AddonCapabilityMask.Control:
                capability = WorldCapability.Control;
                return true;
            case AddonCapabilityMask.Mutate:
                capability = WorldCapability.Mutate;
                return true;
            case AddonCapabilityMask.Edit:
                capability = WorldCapability.Edit;
                return true;
            default:
                capability = default;
                return false;
        }
    }

    /// <summary>Returns the reverse of <see cref="TryCapability"/> — the pinned wire bit a disclosure carries for a held
    /// capability.</summary>
    /// <param name="capability">The engine capability.</param>
    /// <returns>The pinned single-bit mask.</returns>
    public static ulong CapabilityBit(WorldCapability capability) {
        return capability switch {
            WorldCapability.Drive => AddonCapabilityMask.Drive,
            WorldCapability.Observe => AddonCapabilityMask.Observe,
            WorldCapability.Control => AddonCapabilityMask.Control,
            WorldCapability.Mutate => AddonCapabilityMask.Mutate,
            WorldCapability.Edit => AddonCapabilityMask.Edit,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(capability), actualValue: capability, message: "unmapped capability — the wire mapping must be extended deliberately, never defaulted"),
        };
    }
}

/// <summary>
/// The one channel-name resolver the server mounts addons with — <see cref="IAddonChannelResolver"/>'s single
/// World-side implementation, and the one place the guest's channel vocabulary meets the world's. Resolution
/// failure is never a mount fault (an unresolved name is report-and-inert; see <see cref="AddonChannelBinding"/>),
/// so this class does no vocabulary refusal; it only looks a declared name up in the world document's compiled
/// channel table.
/// </summary>
/// <remarks>
/// <para><b>Two ordinal namespaces meet here, and this is the mapping between them.</b> A guest addresses its own
/// declared name table by position: the wire's act verb carries that guest-local index, which never leaves
/// <c>AddonSimulationPump</c> — it is used there only to select the <see cref="AddonChannelBinding"/> the
/// handshake decoded. The ordinal on the binding, and therefore on every act the server folds, is the one
/// <see cref="TryResolve"/> returns: an index into the world's <see cref="WorldChannelTable"/>, where every channel
/// occupies its consecutive document-order slot and role claims are table metadata. The two namespaces never
/// coincide by construction — a guest declaring <c>["forward", "strafe", "jump"]</c> speaks 0/1/2 even when the
/// world's declaration order assigns those names different ordinals — so a folded
/// contribution must only ever be written through a resolved ordinal, never through the guest's own index.</para>
/// <para>The table is the swappable part, the resolver is not: a world's channels section is data, so a different
/// world simply constructs this against a different <see cref="WorldChannelTable"/> — never a second class
/// implementing <see cref="IAddonChannelResolver"/>. If a future change is tempted to add a parallel
/// name→(ordinal, shape) lookup anywhere else in this project, that is the sign to route it through this resolver
/// instead.</para>
/// </remarks>
/// <param name="channels">The boot world document's compiled channel table.</param>
/// <exception cref="ArgumentNullException"><paramref name="channels"/> is <see langword="null"/>.</exception>
internal sealed class WorldAddonChannelResolver(WorldChannelTable channels) : IAddonChannelResolver {
    private readonly WorldChannelTable m_channels = (channels ?? throw new ArgumentNullException(paramName: nameof(channels)));

    /// <inheritdoc/>
    public bool TryResolve(string name, out int ordinal, out AddonChannelValueShape shape) {
        if (!m_channels.TryGetOrdinal(name: name, ordinal: out ordinal)) {
            // The sentinel is restated rather than left as TryGetValue's default 0, which is a REAL ordinal (the
            // first movement role) — a caller reading the out value without the return would otherwise silently
            // drive it.
            ordinal = -1;
            shape = default;

            return false;
        }

        shape = WireShape(shape: m_channels.Shape(ordinal: ordinal));

        return true;
    }

    // The world document's declared shape as the wire's own — the ABI value domain a resolved act is checked
    // against (see AddonSimulationPump.TryValidateInputAct). Total over ChannelShape: a new shape must be given a
    // wire domain deliberately, never defaulted into one of these.
    private static AddonChannelValueShape WireShape(ChannelShape shape) {
        return shape switch {
            ChannelShape.Bipolar => AddonChannelValueShape.Bipolar,
            ChannelShape.Unipolar => AddonChannelValueShape.Unipolar,
            ChannelShape.Binary => AddonChannelValueShape.Binary,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(shape), actualValue: shape, message: "unmapped channel shape — the wire mapping must be extended deliberately, never defaulted"),
        };
    }
}
