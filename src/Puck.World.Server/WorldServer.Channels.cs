using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Resets one seat's per-tick contribution accumulator (the two sums and the contributed held image) — called only
    // when it was actually touched this tick, so an uncontended body's fold phase never pays for a clear it does not
    // need. No ceiling accumulator to clear: the ceiling is durable grant-table state the seat authored, never a
    // per-tick derivation.
    private void ClearContribution(int bodyIndex) {
        m_hasContribution[bodyIndex] = false;
        m_untrustedAcceptedMask[bodyIndex] = default;

        var start = (bodyIndex * ChannelLimits.MaxChannels);

        Array.Clear(
            array: m_untrustedSum,
            index: start,
            length: ChannelLimits.MaxChannels
        );
        Array.Clear(
            array: m_trustedSum,
            index: start,
            length: ChannelLimits.MaxChannels
        );
        Array.Clear(
            array: m_contributedHeld,
            index: start,
            length: ChannelLimits.MaxChannels
        );
    }
    /// <summary>Runs the fold phase (<see cref="FixedContributionFold"/>) once per tick,
    /// after every seat submission and every mounted addon's contribution has landed (see <see cref="Step"/>) and
    /// before the population advances. For each human-occupied local seat that received at least one contribution
    /// this tick, folds its owning seat's own base <c>h</c> (zero when the seat submitted nothing this tick) with the
    /// tick's pooled untrusted sum and unpooled trusted sum, per channel, and calls <see cref="WorldBody.SubmitIntent"/>
    /// once with the composed result — replacing the pass-through write <see cref="ApplyIntentSubmission"/> already
    /// made for the owning seat's own submission. The held-device image is composed the same pass by
    /// <see cref="WorldChannelTable.ComposeHeld"/>'s shape-aware rule (see <see cref="WorldBody.SetHeldChannels"/>: a
    /// unipolar/binary channel joins by maximum — a {0, One} overlay, so a contributor's composition act joins the
    /// seat's the way two simultaneous composition contributors already join inside <see cref="WorldBody"/> — a
    /// bipolar channel instead sums, so a resting contributor can never overwrite a genuinely negative held value).
    /// The pool is the occupying seat's authored limit on how far contributors that human did not authorize may pull
    /// its value away from <c>h</c>. Another co-driving seat is a trusted human tool, so its term is added outside the
    /// pool and consumes none of that ceiling. Occupancy is load-bearing: only a human-occupied body has an owning
    /// seat whose consent can define a pool; an unoccupied bot stays on the full-authority overwrite path.
    /// An occupied body with no contribution this tick (the overwhelming common case) is untouched here: <see
    /// cref="ApplyIntentSubmission"/>'s own direct writes already stand, so this method costs one bool check per
    /// seat.</summary>
    private void FoldChannelContributions() {
        for (var seat = 0; (seat < Population.LocalSeatCount); seat++) {
            if (
                m_hasContribution[seat] &&
                m_population.IsHumanOccupied(bodyIndex: seat) &&
                (Body(index: seat) is { } body)
            ) {
                var h = (m_hasOwnerBase[seat]
                    ? m_ownerBase[seat]
                    : default
                );
                var ownerHeld = (m_hasOwnerBase[seat]
                    ? m_ownerHeld[seat]
                    : default
                );
                var folded = h;
                var held = ownerHeld;
                var baseSlot = (seat * ChannelLimits.MaxChannels);
                // The pool ceilings THIS SEAT authored — one number per channel, read once per folded body. Empty
                // when the seat authored none; StageContribution already refused every untrusted delta in that case,
                // so an empty vector can only be reached with a trusted-only (unpooled) contribution set.
                var ceilings = m_grants.PoolCeilings(
                    seat: WorldPrincipal.Seat(slot: seat),
                    subject: GrantSubject.Body(index: seat)
                );

                var untrustedAccepted = m_untrustedAcceptedMask[seat];

                for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
                    var slot = (baseSlot + ordinal);
                    var untrusted = m_untrustedSum[slot];
                    var trusted = m_trustedSum[slot];
                    var poolCeiling = ceilings[ordinal];
                    var contributedHeld = m_contributedHeld[slot];
                    var shape = (m_population.Channels.IsDeclared(ordinal: ordinal)
                        ? m_population.Channels.Shape(ordinal: ordinal)
                        : ChannelShape.Bipolar
                    );
                    var combinedHeld = WorldChannelTable.ComposeHeld(
                        a: ownerHeld[ordinal].Value,
                        b: contributedHeld,
                        shape: shape
                    );

                    if (combinedHeld != ownerHeld[ordinal].Value) {
                        held = held.WithChannel(
                            ordinal: ordinal,
                            value: FixedQ4816.FromRawBits(value: combinedHeld)
                        );
                    }

                    // The read-back's ceiling in force is poolCeiling ONLY when this tick's contribution set actually
                    // reached this ordinal through the untrusted (pooled) path (m_untrustedAcceptedMask) — an authored
                    // ceiling the seat has on file but nobody exercised THIS write reads back as "no ceiling in
                    // force," never as the number on paper, so body.channels can prove the fold ran rather than
                    // just that a grant exists.
                    var poolReached = untrustedAccepted.Contains(ordinal: ordinal);

                    m_channelReadCeiling[slot] = (poolReached
                        ? poolCeiling
                        : 0L
                    );

                    if (
                        (untrusted == 0L) &&
                        (trusted == 0L)
                    ) {
                        m_channelReadClamped[slot] = false;

                        continue;
                    }

                    var threshold = m_population.Channels.Threshold(ordinal: ordinal);

                    var (minimum, maximum, quantizationThreshold) = WorldChannelTable.CompileFoldShape(
                        shape: shape,
                        threshold: threshold
                    );
                    // WorldGrants uses raw zero as its "no ceiling authored" sentinel, while FixedContributionFold
                    // deliberately reserves zero for a PRESENT zero-width pool. Preserve the old omission semantics:
                    // only a positive authored ceiling becomes a radius; the sentinel becomes null.
                    FixedQ4816? poolRadius = ((poolCeiling > 0L)
                        ? FixedQ4816.FromRawBits(value: poolCeiling)
                        : null
                    );

                    folded = folded.WithChannel(
                        ordinal: ordinal,
                        value: FixedContributionFold.Evaluate(
                            baseline: h[ordinal],
                            poolDeltaRaw: untrusted,
                            outsidePoolDeltaRaw: trusted,
                            poolRadius: poolRadius,
                            minimum: minimum,
                            maximum: maximum,
                            threshold: quantizationThreshold,
                            poolClamped: out var clamped
                        )
                    );
                    m_channelReadClamped[slot] = clamped;
                }

                body.SubmitIntent(intent: folded);
                body.SetHeldChannels(channels: held);

                m_channelReadBase[seat] = h;
                m_channelReadFolded[seat] = folded;
            }

            if (m_hasContribution[seat]) {
                ClearContribution(bodyIndex: seat);
            }

            m_hasOwnerBase[seat] = false;
        }
    }
    // Whether a document-authored `grants` row belongs to the CROSS-DOCUMENT write-back channel rather than to the
    // live table — a `document:<id>` principal, whose capability Server.WorldOwnedWorlds.Decide and
    // TryReadDurableState resolve by reading the OWNER'S DOCUMENT directly. Both replays (the constructor's and the
    // rebuild's) skip these rather than handing them to Grant: the grant table refuses them BY NAME (WorldGrants
    // .Conflicts rule (-1b) — a live row for one is budget-less, mask-less, and read by nothing), so replaying them
    // would print a loud rejection for data the document is CORRECT to carry. Skipping is not hiding them: they are
    // echoed by `world.grants` as document-authored rows, which is where they actually live and act.
    private static bool IsDocumentChannelRow(WorldGrant grant) => (grant.Principal.Kind == PrincipalKind.Document);
    // Find-or-add PRINCIPAL's read-back contributor row within bodyIndex's slice, merging channel-mask bits when the
    // SAME principal reaches this method more than once THIS tick (a guest whose separate acts each touch one
    // channel). Past MaxReadContributorsPerSeat the read-back saturates — the same diagnostic-degrades trade
    // ReportContention makes above — rather than resizing on the contribution path.
    private void RecordContributor(int bodyIndex, WorldPrincipal principal, bool trusted, ChannelHeldMask channelMask) {
        var baseSlot = (bodyIndex * MaxReadContributorsPerSeat);
        var count = m_channelReadContributorCount[bodyIndex];

        for (var index = 0; (index < count); index++) {
            var slot = (baseSlot + index);

            if (m_channelReadContributor[slot] == principal) {
                m_channelReadContributorMask[slot] = m_channelReadContributorMask[slot].Union(other: channelMask);

                return;
            }
        }

        if (count < MaxReadContributorsPerSeat) {
            var slot = (baseSlot + count);

            m_channelReadContributor[slot] = principal;
            m_channelReadContributorTrusted[slot] = trusted;
            m_channelReadContributorMask[slot] = channelMask;
            m_channelReadContributorCount[bodyIndex] = (count + 1);
        }
    }
    // Resets bodyIndex's read-back to "no pool, no contributors" and records the direct-write outcome (h == folded ==
    // the intent SubmitIntent actually received) — the owning seat's own write in ApplyIntentSubmission, before
    // FoldChannelContributions gets a chance to run for this seat this same tick. Left standing when no contribution
    // ever lands; overwritten by the real fold breakdown when one does (see FoldChannelContributions).
    private void RecordDirectChannelRead(int seat, PlayerIntent intent) {
        m_channelReadBase[seat] = intent;
        m_channelReadFolded[seat] = intent;
        m_channelReadContributorCount[seat] = 0;

        var start = (seat * ChannelLimits.MaxChannels);

        Array.Clear(
            array: m_channelReadCeiling,
            index: start,
            length: ChannelLimits.MaxChannels
        );
        Array.Clear(
            array: m_channelReadClamped,
            index: start,
            length: ChannelLimits.MaxChannels
        );
    }
    // Buffers one non-owning principal's contribution to a human-occupied body's per-tick contribution set, raw
    // Int64 accumulation only (see FixedContributionFold's remarks on why: never through a saturating operator).
    // BOTH halves of the submission ride: the movement/analog `Intent` accumulates into the tick's sums, and the
    // HeldChannels composition image accumulates into m_contributedHeld via WorldChannelTable.ComposeHeld's shape rule
    // — max for unipolar/binary, RAW UNCLAMPED SUM for bipolar (see that method's own remarks on why this accumulator
    // must not clamp per contributor) — a contributor's composition act is an act; dropping it here would make a
    // guest's press vanish the moment a tape drives the body it is pressing on.
    //
    // TRUSTED-BY-AUTHORSHIP: classification keys on HOST LOCUS, not on
    // principal KIND by coincidence of vocabulary alone. THREE terms exist today:
    //   - Console/Seat (another seat co-driving the body it does not own; a console press once one reaches this
    //     path): a human's own tool, added OUTSIDE the pool, wholly UNMASKED — no reach, no ceiling.
    //   - A document-mounted Addon: WORLD LOGIC authored by the world itself (every mounted addon today runs on
    //     Puck.World.Addons/WorldAddonRuntime — the Simulation lane — so PrincipalKind.Addon alone already names
    //     that host locus; a FUTURE client-hosted addon would need its own kind here, never a silent share of this
    //     one). Also added OUTSIDE the pool — consent does not apply to world logic (a world doesn't ask permission
    //     to apply wind) — but unlike Console/Seat its term still respects its OWN declared Reach (DATA describing
    //     which channels the world logic touches, never a security boundary the occupying seat must consent to): an
    //     addon that declares nothing still contributes nothing. Fuel/budget remain robustness bounds regardless
    //     (WorldGrants' metering of an untrusted-for-administration principal is unchanged by this reclassification).
    //   - Genuinely untrusted principals (a Peer today; a future client-hosted addon would join this branch) stay
    //     POOLED under Reach ∧ Consent exactly as before: default-deny per channel, needing BOTH the contributor's
    //     own row to REACH the channel AND the OCCUPYING SEAT to have authored a ceiling for it. A channel missing
    //     either contributes nothing, silently — the addon's own act already carries a verdict from
    //     WorldAddonRuntime.FoldActs; a per-channel miss is a quieter refinement of the same "requested, not
    //     received" shape, not a second refusal channel. An ordinal accepted through the POOLED branch alone marks
    //     m_untrustedAcceptedMask, regardless of the delta's own value — a cancelling pair of contributors must
    //     still read back as "the pool was reached," never as "nothing happened" (body.channels' ceiling report).
    private void StageContribution(int bodyIndex, WorldPrincipal principal, in IntentSubmission submission) {
        var isConsoleOrSeat = (principal.Kind is PrincipalKind.Console or PrincipalKind.Seat);
        var isAddon = (principal.Kind == PrincipalKind.Addon);
        var trustedInFold = (isConsoleOrSeat || isAddon);
        var subject = GrantSubject.Body(index: bodyIndex);
        var reach = default(ChannelReachMask);
        var hasReach = (!isConsoleOrSeat && m_grants.TryGetChannelReach(
            mask: out reach,
            principal: principal,
            subject: subject
        ));
        // The occupying seat's OWN authored ceilings only ever bound the POOLED (genuinely untrusted) path — a
        // trusted addon's own declared Reach is the whole gate; there is no seat consent for it to consult.
        var ceilings = ((!isConsoleOrSeat && !isAddon)
            ? m_grants.PoolCeilings(
                seat: WorldPrincipal.Seat(slot: bodyIndex),
                subject: subject
            )
            : default
        );
        var eligible = (!hasReach
            ? default
            : (isAddon
                ? new ChannelHeldMask(Bits: reach.Bits)
                : reach.Meet(consent: ceilings.Support)
        ));

        if (!m_hasContribution[bodyIndex]) {
            // First touch THIS tick for this seat: the read-back's contributor list starts a fresh episode here — the
            // one place a new episode's stale rows (left over from whichever earlier tick last touched this seat) must
            // be dropped before RecordContributor appends fresh ones. ClearContribution (below) wipes the sums and
            // held image once the fold has READ them (no ceiling: that is durable grant-table state, not a per-tick
            // accumulator); it never touches the read-back, which must survive past that point.
            m_channelReadContributorCount[bodyIndex] = 0;
        }

        m_hasContribution[bodyIndex] = true;

        var baseSlot = (bodyIndex * ChannelLimits.MaxChannels);
        var acceptedMask = default(ChannelHeldMask);
        var untrustedAccepted = m_untrustedAcceptedMask[bodyIndex];

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            var slot = (baseSlot + ordinal);
            var delta = submission.Intent[ordinal].Value;
            var held = submission.HeldChannels[ordinal].Value;

            if (
                (delta == 0L) &&
                (held == 0L)
            ) {
                continue;
            }

            var shape = (m_population.Channels.IsDeclared(ordinal: ordinal)
                ? m_population.Channels.Shape(ordinal: ordinal)
                : ChannelShape.Bipolar
            );
            var isBipolar = (shape == ChannelShape.Bipolar);

            if (
                isConsoleOrSeat ||
                (isAddon && eligible.Contains(ordinal: ordinal))
            ) {
                // Trusted, outside the pool: Console/Seat unmasked; a document-mounted Addon gated by its OWN
                // declared Reach only.
                m_trustedSum[slot] += delta;
                // Deferred clamp: bipolar sums raw and unclamped (WorldChannelTable.ComposeHeld applies the ONE clamp
                // later, in FoldChannelContributions); unipolar/binary max as before.
                m_contributedHeld[slot] = (isBipolar
                    ? (m_contributedHeld[slot] + held)
                    : Math.Max(
                        val1: m_contributedHeld[slot],
                        val2: held
                    )
                );
                acceptedMask = acceptedMask.With(ordinal: ordinal);
            } else if (
                !trustedInFold &&
                eligible.Contains(ordinal: ordinal)
            ) {
                // Genuinely untrusted (pooled): Reach ∧ Consent.
                m_untrustedSum[slot] += delta;
                m_contributedHeld[slot] = (isBipolar
                    ? (m_contributedHeld[slot] + held)
                    : Math.Max(
                        val1: m_contributedHeld[slot],
                        val2: held
                    )
                );
                acceptedMask = acceptedMask.With(ordinal: ordinal);
                untrustedAccepted = untrustedAccepted.With(ordinal: ordinal);
            }
        }

        m_untrustedAcceptedMask[bodyIndex] = untrustedAccepted;

        if (!acceptedMask.IsEmpty) {
            RecordContributor(
                bodyIndex: bodyIndex,
                channelMask: acceptedMask,
                principal: principal,
                trusted: trustedInFold
            );
        }
    }
}
