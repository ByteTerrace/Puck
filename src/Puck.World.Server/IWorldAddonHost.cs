using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The seam <see cref="WorldServer"/> and <see cref="WorldReplaySnapshot"/> pump the mounted addon guests
/// through — every member either project calls on the concrete host today. <c>Addons.WorldAddonRuntime</c> is the
/// one implementation; it also carries <c>Reload</c>/<c>SetEnabled</c>/<c>DescribeCost</c> on its concrete surface,
/// deliberately not on this interface, because those return/accept <c>Puck.Scripting</c> shapes
/// (<c>AddonCostReport</c> embeds <c>AddonState</c>) that this project must not name — their callers hold the
/// concrete type as a composition-root DI dependency instead. <see cref="IDisposable"/> because
/// <see cref="WorldReplaySnapshot"/>'s offline shadow-world re-drive owns a fresh host per drive
/// (<c>using var addons = addonHostFactory(...)</c>).</summary>
public interface IWorldAddonHost : IDisposable {
    /// <summary>Whether any mounted guest has ever had an admitted <c>TickAddons</c> pump — the boot-anchored replay
    /// arm predicate.</summary>
    bool AnyEverPumped { get; }
    /// <summary>The number of guests currently mounted — the sizing bound
    /// <see cref="WorldServer.AttachAddons"/> reads for its per-tick contention tracking.</summary>
    int MountedCount { get; }
    /// <summary>Every mounted guest's recorded-at-mount identity, in mount order.</summary>
    IReadOnlyList<WorldAddonReceipt> Receipts { get; }

    /// <summary>Resolves Drive handles, checks authority, and submits the folded intent for every mounted guest —
    /// the second of the three tick-boundary pump points, run after the intent drain.</summary>
    /// <param name="tick">The tick about to advance.</param>
    void ApplyContributions(ulong tick);
    /// <summary>Stages the wire verdict for one addon-sourced mutation into its originating guest's reserved answer
    /// cell, at drain time — never the mutation's application, which already ran through the same door a console
    /// mutation runs through.</summary>
    /// <param name="addonIndex">The mounted addon index the mutation was decoded from.</param>
    /// <param name="actOrdinal">The guest-assigned act ordinal the answer cell is keyed by.</param>
    /// <param name="applied">Whether the document-apply pipeline accepted the decoded mutation.</param>
    void CompleteMutation(int addonIndex, ushort actOrdinal, bool applied);
    /// <summary>Reports the channels a co-driving grant confers on <paramref name="principal"/> that the guest never
    /// declares, or <see langword="null"/> when every granted channel is declared, the principal names no mounted
    /// guest, or the grant carries no channel mask.</summary>
    /// <param name="principal">The principal the grant confers on.</param>
    /// <param name="reach">The grant's channel reach, when it carries one.</param>
    /// <param name="channels">The world's channel table, for naming the ordinals.</param>
    string? DescribeUndeclaredGrantedChannels(WorldPrincipal principal, ChannelReachMask? reach, WorldChannelTable channels);
    /// <summary>Live-mounts a new guest — the runtime half of the <c>world.addon.mount</c> lifecycle submission.
    /// Returns a human-readable status line; a leading single-quote (<c>'name' ...</c>) is the load-bearing
    /// rejection convention <see cref="WorldServer"/>'s <c>TryApplyAddonLifecycle</c> reads to turn the status back
    /// into a <c>Rejected</c>/<c>Denied</c>-shaped echo.</summary>
    /// <param name="name">The descriptor name to mount under.</param>
    /// <param name="modulePath">The module's file path.</param>
    /// <param name="hash">The module's pinned <c>sha256-64/{hex}</c> content identity.</param>
    /// <param name="fuel">The per-tick fuel budget the instance runs under.</param>
    /// <param name="requests">The manifest's requested (capability, subject) pairs, or <see langword="null"/> for
    /// none.</param>
    string Mount(string name, string modulePath, string hash, ulong fuel, IReadOnlyList<WorldCapabilityRequest>? requests);
    /// <summary>Writes the guest's input ring, runs <c>puck_on_tick</c>, and decodes/vocabulary-validates its output
    /// — the first of the three tick-boundary pump points, run at the very top.</summary>
    /// <param name="tick">The tick about to advance.</param>
    void TickAddons(ulong tick);
    /// <summary>Fully unmounts a guest by name — stronger than a disable: the guest leaves
    /// <see cref="Receipts"/> and <see cref="MountedCount"/> entirely.</summary>
    /// <param name="name">The mounted guest's name.</param>
    /// <returns>A human-readable status line, using the same leading-quote rejection convention as
    /// <see cref="Mount"/>.</returns>
    string Unmount(string name);
    /// <summary>Resolves disclosures, world events, asks, and pose queries against the post-step authoritative
    /// state, staged for the next tick's batch — the third of the three tick-boundary pump points, run after the
    /// population advances.</summary>
    /// <param name="tick">The tick that just advanced.</param>
    void ResolveReads(ulong tick);
}
