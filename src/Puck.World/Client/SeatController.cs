using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// One local seat's device-intent producer: the held channel contributions and the analog stick samples —
/// everything a seat's physical devices stage between ticks.
/// <see cref="HeldIntent"/> folds the producers into the per-tick <see cref="PlayerIntent"/> the client submits to the
/// authoritative server; <see cref="HeldChannels"/> is the always-overlay device-channel image riding the same
/// submission (composition ordinals only — movement roles ride <see cref="HeldIntent"/> directly). The seat's
/// authoritative body lives server-side — this type never integrates a pose.
/// </summary>
/// <remarks>Single-threaded: every mutator runs during the command pump's apply window and the per-tick submission
/// reads immediately after, both on the launcher's window-pump thread, so no lock guards this state.</remarks>
internal sealed class SeatController {
    private static readonly FixedQ4816 s_negativeOne = -FixedQ4816.One;

    // The device-image fold primitive per channel ordinal: base zero, contributions are (control value × scale), no
    // pool, accumulate in RAW Int64 and clamp EXACTLY ONCE at the end. A saturating clamp per contribution is
    // commutative but NOT associative — order-dependent near the ceiling. Producing the device image IS the fold
    // primitive under a degenerate configuration, never a second merge
    // rule beside it — which is why holding W and S while a stick reports +0.3 still yields +0.3, where a sign-group
    // or max-of-group rule would have to be invented (and would get that case wrong) to reproduce it.
    // Keyed by the CONTRIBUTING CONTROL's identity (the binding source, e.g. "keyboard.w"), never by
    // (ordinal, scale): two controls sharing one ordinal at the SAME scale (W and a redundant Up-arrow row) must hold
    // INDEPENDENTLY — releasing one must never silently drop the other's contribution, which a bare (ordinal, scale)
    // set could not tell apart. Opposing scales on one ordinal (W=+1, S=-1) still cancel, by summing.
    private readonly Dictionary<string, (int Ordinal, FixedQ4816 Scale)> m_heldControls = [];
    // The analog producer's latest sample, routed from this tick's snapshot. InputRouter re-dispatches a carried analog
    // value every tick; ClearAnalog wipes this local staging state after the tick so only snapshot input can refill it.
    private FixedQ4816 m_analogMoveX;
    private FixedQ4816 m_analogMoveY;
    private FixedQ4816 m_analogLookX;
    private FixedQ4816 m_analogLookY;
    // The frame-visible camera-look sample: promoted from the tick-local right stick immediately before staging is
    // cleared, then held stable until the next tick. The render clock integrates this presentation-only latch;
    // whether X ALSO enters authoritative Turn is selected by HeldIntent's authored camera-yaw policy.
    // The client copy of the seat's intent source: device edges and the held-intent submission run only under Live,
    // mirroring the server body's merge rule.
    private IntentSource m_source = IntentSource.Live;
    // The world's declared channel shapes — HeldChannels' only source for a composition ordinal's fold range
    // (bipolar/unipolar/binary). Defaults to the empty table (every composition ordinal falls back to the widest,
    // non-lossy bipolar range) so a seat built before the table is threaded in never silently drops a negative
    // contribution the way a hardcoded [0, One] used to.
    private WorldChannelTable m_channels = WorldChannelTable.Empty;

    /// <summary>The profile this seat selects — the client-side identity (color and look-invert). The server body holds
    /// its own reference for speeds, assigned over the session wire.</summary>
    public WorldIdentity? Profile { get; set; }

    /// <summary>The seat-lifetime logical view state shared by input, movement, every renderer, and read-back.</summary>
    public WorldSeatViewState View { get; } = new();

    /// <summary>The world's declared channel table — resolves each composition ordinal's shape for
    /// <see cref="HeldChannels"/>'s end clamp. Set once by the roster from the same table the server compiled
    /// (<c>WorldServer.Population.Channels</c>); <see langword="null"/> is normalized to
    /// <see cref="WorldChannelTable.Empty"/>.</summary>
    public WorldChannelTable Channels {
        get => m_channels;
        set => m_channels = (value ?? WorldChannelTable.Empty);
    }

    /// <summary>The seat's client-side intent-source copy (matches the server body's; both are written by
    /// <c>player.control</c>).</summary>
    public IntentSource Source => m_source;

    /// <summary>This tick's live-held device-channel image, submitted alongside <see cref="HeldIntent"/> — derived
    /// from the SAME held-control set <see cref="HeldIntent"/> reads, restricted to non-role ordinals; movement roles
    /// ride <see cref="HeldIntent"/> directly, never this image. Every held control's contribution to
    /// one ordinal sums in raw <see cref="FixedQ4816"/> storage, then runs through
    /// <see cref="FixedContributionFold.Evaluate"/> once against that ordinal's declared shape
    /// range (bipolar <c>[-One, One]</c>; unipolar or binary <c>[0, One]</c> — a binary channel's PRE-quantization
    /// pool-clamp domain per its own remarks, since bit-quantization is the server's job, never the client's).</summary>
    public PlayerIntent HeldChannels {
        get {
            if (m_heldControls.Count == 0) {
                return default;
            }

            Span<long> raw = stackalloc long[ChannelLimits.MaxChannels];

            foreach (var (ordinal, scale) in m_heldControls.Values) {
                if (m_channels.IsRole(ordinal: ordinal)) {
                    continue;
                }

                raw[ordinal] += scale.Value;
            }

            var channels = default(ChannelValues);

            for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
                if (m_channels.IsRole(ordinal: ordinal)) {
                    continue;
                }
                if (raw[ordinal] == 0L) {
                    continue;
                }

                var shape = (m_channels.IsDeclared(ordinal: ordinal) ? m_channels.Shape(ordinal: ordinal) : ChannelShape.Bipolar);

                var (minimum, maximum, _) = WorldChannelTable.CompileFoldShape(shape: shape, threshold: m_channels.Threshold(ordinal: ordinal));

                // A seat's held device image is the no-pool specialization: zero baseline, the completed raw device
                // sum in the pool-delta slot, no outside-pool term, and deliberately no binary threshold. Binary is
                // continuous [0, One] here; authoritative composition performs the terminal bit quantization.
                channels[ordinal] = FixedContributionFold.Evaluate(
                    baseline: FixedQ4816.Zero,
                    poolDeltaRaw: raw[ordinal],
                    outsidePoolDeltaRaw: 0L,
                    poolRadius: null,
                    minimum: minimum,
                    maximum: maximum,
                    threshold: null,
                    poolClamped: out _
                );
            }

            return new PlayerIntent(Channels: channels);
        }
    }

    /// <summary>This frame's movement-stick sample (left stick), already quantized at the router seam — no simulation
    /// consumer reads a float form. Zero once <see cref="ClearAnalog"/> has run for the frame — a live read only sees
    /// a non-zero value while a routed dispatch has set it earlier in the same command pump (i.e. the stick is
    /// actively deflected). The <c>player.sticks</c> observability verb is the one site that converts this back to a
    /// float for display.</summary>
    public FixedVector2 AnalogMove => new(X: m_analogMoveX, Y: m_analogMoveY);
    /// <summary>This frame's look-stick sample (right stick); see <see cref="AnalogMove"/> for the freshness caveat
    /// and the float-echo site.</summary>
    public FixedVector2 AnalogLook => new(X: m_analogLookX, Y: m_analogLookY);
    /// <summary>The look stick's frame-visible sample, promoted by <see cref="ClearAnalog"/> before the tick-local
    /// staging is wiped. Presentation reads this stable latch between simulation ticks; it never feeds simulation
    /// through this property.</summary>

    /// <summary>Asserts a channel contribution as held, keyed by the CONTROL holding it — never by (ordinal, scale)
    /// alone, so a second physical control sharing this ordinal (even at the identical scale) holds independently of
    /// the first, and so an analog control's magnitude can update in place every re-dispatch tick without leaking a
    /// stale (ordinal, scale) pair under a different key. Idempotent per control — a key held down and auto-repeating
    /// (or an unchanged analog re-dispatch) re-asserts the same entry with no effect.</summary>
    /// <param name="controlId">The contributing control's identity — the binding source (e.g. <c>"keyboard.w"</c>);
    /// <see langword="null"/> or empty is normalized to a shared fallback key for a caller with no physical source
    /// (an injected/synthesized hold).</param>
    /// <param name="ordinal">The channel ordinal this control contributes to.</param>
    /// <param name="scale">This control's current scale/sample (e.g. <c>+One</c> for W, <c>-One</c> for S on the same "forward" ordinal).</param>
    public void HoldChannel(string? controlId, int ordinal, FixedQ4816 scale) {
        m_heldControls[(controlId ?? string.Empty)] = (ordinal, scale);
    }
    /// <summary>Releases the channel contribution held under <paramref name="controlId"/>. A no-op if that control
    /// holds nothing — in particular, releasing one control never touches a DIFFERENT control's entry, even one on
    /// the same ordinal at the same scale (see <see cref="HoldChannel"/>).</summary>
    /// <param name="controlId">The releasing control's identity, matching the one <see cref="HoldChannel"/> was called with.</param>
    public void ReleaseChannel(string? controlId) {
        _ = m_heldControls.Remove(key: (controlId ?? string.Empty));
    }
    /// <summary>Feeds this frame's movement (left) stick sample, already quantized to fixed point at the router seam
    /// (see <see cref="Puck.Commands.CommandValueQuantization.QuantizeAxis"/>) and deadzoned/normalized to <c>[-1, 1]</c>
    /// by the platform layer (+Y forward, +X strafe right). Set by the roster's per-device router while a dispatch is
    /// live; a centered stick emits no dispatch, so the value is wiped by <see cref="ClearAnalog"/> each frame
    /// (consume-then-clear, so a disconnected pad never leaves a stale deflection behind). No float conversion
    /// happens here — the value arrives already quantized, once, and is stored verbatim.</summary>
    /// <param name="move">The already-quantized movement stick sample.</param>
    public void SetAnalogMove(FixedVector2 move) {
        m_analogMoveX = move.X;
        m_analogMoveY = move.Y;
    }
    /// <summary>Feeds this frame's look (right) stick sample: +X looks right and +Y looks up. A heading-frame seat
    /// may additionally route X to authoritative Turn; an absolute-orbit seat consumes it only in presentation.
    /// Same consume-then-clear contract and already-quantized-at-the-door contract as <see cref="SetAnalogMove"/>.</summary>
    /// <param name="look">The already-quantized look stick sample (+X looks right, +Y looks up).</param>
    public void SetAnalogLook(FixedVector2 look) {
        m_analogLookX = look.X;
        m_analogLookY = look.Y;
    }
    /// <summary>Promotes the look stick to the presentation latch, then wipes both tick-local analog samples to zero. Called
    /// once per tick AFTER submission has consumed them: a centered stick dispatches nothing, so the next promotion
    /// replaces the latch with zero rather than leaving a stale deflection behind.</summary>
    public void ClearAnalog() {
        m_analogMoveX = FixedQ4816.Zero;
        m_analogMoveY = FixedQ4816.Zero;
        m_analogLookX = FixedQ4816.Zero;
        m_analogLookY = FixedQ4816.Zero;
    }
    /// <summary>Sets the client-side intent-source copy — <c>player.control</c>'s seat half (the server body's axis is
    /// written by the same command). A transition drops the live device holds via <see cref="ReleaseAllHeld"/>, so
    /// nothing leaks through a source switch or bursts when Live returns. A no-op if the source is unchanged.</summary>
    /// <param name="source">The intent source to latch.</param>
    public void SetIntentSource(IntentSource source) {
        if (source == m_source) {
            return;
        }

        m_source = source;
        ReleaseAllHeld();
    }
    /// <summary>Releases every held movement contribution and live-held composition channel. Called when a
    /// possession/engagement latch transitions, when the keyboard leaves this seat (a still-down key's release edge
    /// routes to the keyboard's new slot, so the source would walk forever), and by <c>player.stop</c>'s seat half.</summary>
    public void ReleaseAllHeld() {
        // A single Clear covers both movement and composition holds — a still-down Space would otherwise stick the
        // jump channel held, exactly the hazard clearing only the movement set would reintroduce.
        m_heldControls.Clear();
    }
    /// <summary>Folds the live producers — the held-control set and the analog sticks — into the tick's submitted
    /// intent: peers summed then clamped, so opposing inputs cancel and a key plus a full stick never exceeds full
    /// deflection. Stick up (+Y) is forward. In heading-camera mode, look-stick right (+X) also turns the body in the
    /// presentation camera. The right stick never writes authoritative Turn. All
    /// six role channels fold identically (MoveUp/Pitch/Roll alongside the
    /// original three) — a <see cref="Puck.World.Server.WorldBody"/> running a grounded body motion program
    /// simply never reads the extra three, exactly like an unbound composition channel; a document declaring them
    /// (required for a free-attitude body motion program, see <c>WorldDefinitionValidator</c>) is the only way they drive
    /// anything, so wiring them through here never changes Grounded behavior.</summary>
    public PlayerIntent HeldIntent() {
        // No controls held (the common case — an idle seat): skip the held-set walk and fold the analog sample
        // straight through.
        if (m_heldControls.Count == 0) {
            return m_channels.RoleOrdinals.Intent(
                moveForward: FixedQ4816.Clamp(value: m_analogMoveY, minimum: s_negativeOne, maximum: FixedQ4816.One),
                moveStrafe: FixedQ4816.Clamp(value: m_analogMoveX, minimum: s_negativeOne, maximum: FixedQ4816.One),
                turn: FixedQ4816.Zero
            );
        }

        // The role-channel fold primitive, mirroring HeldChannels' vector accumulate above: seed raw with the three
        // analog samples, sum every held role contribution into raw[ordinal] (RAW Int64, no per-add clamp — see
        // HeldChannels' remarks on why a saturating clamp per contribution is order-dependent), then clamp each role
        // EXACTLY ONCE below.
        // [-One, One] is safe on every role ordinal below (here and in the no-held-controls fold above) because every
        // role channel IS bipolar by validator rule (WorldDefinitionValidator.ValidateChannels refuses any other
        // declared shape on a role channel).
        Span<long> raw = stackalloc long[ChannelLimits.MaxChannels];
        var roles = m_channels.RoleOrdinals;

        if (roles.MoveForward >= 0) {
            raw[roles.MoveForward] = m_analogMoveY.Value;
        }
        if (roles.MoveStrafe >= 0) {
            raw[roles.MoveStrafe] = m_analogMoveX.Value;
        }
        foreach (var (ordinal, scale) in m_heldControls.Values) {
            if (!m_channels.IsRole(ordinal: ordinal)) {
                continue;
            }

            raw[ordinal] += scale.Value;
        }

        return roles.Intent(
            moveForward: ClampedRaw(raw: raw, ordinal: roles.MoveForward),
            moveStrafe: ClampedRaw(raw: raw, ordinal: roles.MoveStrafe),
            turn: ClampedRaw(raw: raw, ordinal: roles.Turn),
            moveUp: ClampedRaw(raw: raw, ordinal: roles.MoveUp),
            pitch: ClampedRaw(raw: raw, ordinal: roles.Pitch),
            roll: ClampedRaw(raw: raw, ordinal: roles.Roll)
        );
    }

    private static FixedQ4816 ClampedRaw(ReadOnlySpan<long> raw, int ordinal) => ((ordinal >= 0)
        ? FixedQ4816.Clamp(value: FixedQ4816.FromRawBits(value: raw[ordinal]), minimum: s_negativeOne, maximum: FixedQ4816.One)
        : FixedQ4816.Zero);
}
