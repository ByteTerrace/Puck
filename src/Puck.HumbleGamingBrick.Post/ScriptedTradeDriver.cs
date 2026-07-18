using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The scripted two-machine cross-gen-cart Cable Club trade, implemented as a peek-gated phase machine. Unlike the
/// fixed-tape <see cref="LinkInputScript"/> the cross-generation replay gate uses, a real menu-navigation walk through a
/// commercial game cannot be a frozen keyframe tape: the same A-taps that reach one state under one stepping cadence
/// land in the wrong menu under another. So every phase runs a mash/hold pattern UNTIL a WRAM/HRAM/SRAM condition
/// (<see cref="ISystemBus.ReadByte"/> peeks) is observed, then the next phase begins — the honest way to gate on in-game
/// progress. The whole machine is a pure function of frame index and peeked state (no wall clock, no RNG), so a linked
/// run driven through a <see cref="SerialLinkSession"/> is replay-identical, exactly the property the
/// <c>link-lock</c> gate rests on.
/// <para>
/// MANDATED symmetry-break: two near-identical Cgb machines running identical mash tapes can deterministically livelock
/// the <c>WaitForLinkedFriend</c> rDIV-jitter master/slave rendezvous (both keep declaring master from perfectly-synced
/// boot states). Side B's receptionist A-mash is staggered <see cref="RendezvousStaggerFrames"/> frames behind side A's,
/// breaking the symmetry so the rendezvous resolves to exactly one <c>$01</c> (external/slave) and one <c>$02</c>
/// (internal/master) at <see cref="ScriptedTradeHarness.SerialConnectionStatusAddress"/>.
/// </para>
/// </summary>
internal sealed class ScriptedTradeDriver {
    // --- Tunables (authored/verified with --trade-run against the trade cart, USA) ---

    /// <summary>Side B's receptionist mash lags side A's by this many frames — the mandated rendezvous symmetry-break.</summary>
    public const int RendezvousStaggerFrames = 97;

    /// <summary>Side B's persistent DIV head start (T-cycles) applied before the cable connects — half a DIV bit-7 period
    /// so the two machines always read opposite DIV bit 7 at WaitForLinkedFriend's jitter check, breaking the master/slave
    /// symmetry. The DIV counter's bit 7 (of the visible register) toggles every 32768 T-cycles.</summary>
    public const ulong RendezvousDivOffsetCycles = 32768;

    // A-mash cadence: press MashPress frames, release the rest of a MashPeriod window, so the cart's edge-triggered menus see
    // one clean press per period (a held button registers a single edge; mash = alternating press/release).
    private const int MashPeriod = 8;
    private const int MashPress = 2;

    // Per-phase frame budgets — generous ceilings (the machine runs ~250 fps headless, so long waits are cheap); a phase
    // that blows its ceiling without meeting its peek condition is a livelock/desync, failed fast.
    private const int ContinueBudget = 900;
    private const int FaceUpBudget = 40;

    // The overworld loads its map by ~frame 450 but is still fading in / not accepting input for a while after; the
    // player can only be turned once it has settled (verified with --trade-talk: ~600 frames of settle before the tap-UP
    // takes). The Continue phase holds until both machines pass this frame.
    private const int ContinueSettleFrame = 600;

    // wPlayerDirection ($D205) reads 4 when the player faces UP toward the attendant (0 = DOWN, the crafted default).
    private const byte PlayerDirectionUp = 4;
    private const int ApproachBudget = 400;
    private const int ReceptionistBudget = 12000;

    // TRADE_CENTER seat geometry (maps/TradeCenter.asm). The two console bg_events on row Y=4 are (4,4) BGEVENT_RIGHT and
    // (5,4) BGEVENT_LEFT; a directional bg_event fires when the player stands on the tile ADJACENT to the console FACING
    // it (home/map.asm CheckFacingBGEvent → GetFacingTileCoord + the .checkdir facing match), and (4,4)/(5,4) are solid
    // furniture. So each side stands on the CHRIS-avatar tile its map callback vacated: the external-clock side ($01,
    // side A) has CHRIS2 removed from (6,4) and stands there facing LEFT (facing tile (5,4) → its BGEVENT_LEFT); the
    // internal-clock side ($02, side B) has CHRIS1 removed from (3,4) and stands there facing RIGHT (facing tile (4,4)).
    private const byte SeatY = 4;
    private const byte CorridorY = 5;
    private const byte FacingLeft = 0x08;
    private const byte FacingRight = 0x0C;
    private const byte SideASeatX = 6;
    private const byte SideBSeatX = 3;
    private const ushort WXCoordAddress = 0xDA03;
    private const ushort WYCoordAddress = 0xDA02;

    // Firing the console: the seat A-press only registers once the tap-turn's animation has finished (a press mid-turn
    // is eaten), so the Console phase MASHES A per side until that side's script engine is actually running — pressing
    // anything else before then risks turning/walking off the seat (the exact failure a blind fixed cycle produced).
    private const int ConsoleBudget = 900;

    // The whole in-`special TradeCenter` interaction (block exchange, party menu, submenu, confirm, animation, auto-save)
    // ceiling — generous; the phase ends early the frame the leads swap.
    private const int TradeMenuBudget = 6000;

    // Cancelling out: the post-trade animation + auto-save + re-entry into the party menu run for hundreds of frames
    // (the engine sprinkles 100-frame delays) before any input registers, then BOTH sides must select the CANCEL footer
    // (LinkTradeOTPartymonMenuCheckCancel holds each side until the partner also sends the $F cancel action).
    private const int CancelBudget = 3600;

    // The cancel-drive input cycle (frames): DOWN (player party -> OT party), settle, DOWN (OT party -> the CANCEL
    // footer; a stray extra DOWN is ignored in the cancel state), settle, A (send the $F cancel action), long settle
    // (the cancel handshake exchanges a sync nybble and each side re-arms until BOTH have sent $F). The trade menu's
    // joypad filter is A|UP|DOWN — B does nothing here, which is why a B-mash never exits.
    private const int CancelCycle = 90;

    // The trade-menu input cycle (frames): tap A (open the party-mon submenu / advance / confirm), settle, tap RIGHT (move
    // the STATS|TRADE cursor to TRADE), settle, tap A (select TRADE), then a long settle for the block exchange + partner
    // nybble sync + trade animation. Cycling makes it robust to those data-dependent waits.
    private const int TradeMenuCycle = 90;
    private const ushort SerialControlAddress = 0xFF02;

    // TRADE_CENTER is map group 20 / map 2 (§2.5) — the console room the room-match warp lands both players in.
    private const byte TradeCenterGroup = 20;
    private const byte TradeCenterMap = 2;

    // wLinkMode == LINK_TRADECENTER (2) once CheckBothSelectedSameRoom succeeds — the link is fully established.
    private const byte LinkTradeCenter = 2;

    /// <summary>Builds and runs the full scripted trade to completion (or the first phase that fails its peek condition),
    /// returning the states observed, both sides' serial-traffic fingerprints, and both final snapshots. The optional
    /// <paramref name="churnAtStep"/> injects a credit-preserving suspend/snapshot/restore/reconnect at that global frame
    /// (which must be transfer-idle), proving the trade is transparent to a snapshot cycle; <paramref name="probes"/>, when
    /// supplied, is filled with a per-frame idle/phase probe so a caller can pick a mid-trade idle churn boundary.</summary>
    /// <param name="rom">The trade-cart ROM image used by both machines.</param>
    /// <param name="churnAtStep">The global frame at which to suspend, snapshot, restore, and reconnect, or -1 to
    /// disable churn.</param>
    /// <param name="probes">An optional destination for per-frame idle and phase observations.</param>
    /// <param name="onFrame">An optional callback invoked for each observed frame.</param>
    /// <param name="attemptTrade">When <see langword="true"/> (the <c>link-lock</c> gate and the interactive
    /// <c>--trade-run</c> explorer) the driver continues past the established link into the TRADE_CENTER console
    /// navigation + full mon-selection trade, through the post-trade CANCEL back out of the link; when
    /// <see langword="false"/> it stops after both machines establish the Trade Center link.</param>
    public static TradeResult Run(
        byte[] rom,
        int churnAtStep = -1,
        List<TradeProbe>? probes = null,
        Action<TradeFrame>? onFrame = null,
        bool attemptTrade = true
    ) {
        var driver = new ScriptedTradeDriver(rom: rom, attemptTrade: attemptTrade);

        return driver.Drive(churnAtStep: churnAtStep, probes: probes, onFrame: onFrame);
    }

    private readonly byte[] m_rom;
    private MachineInstance m_a;
    private MachineInstance m_b;
    private readonly TrafficTally m_tallyA = new();
    private readonly TrafficTally m_tallyB = new();
    private readonly bool m_attemptTrade;
    private Phase m_phase = Phase.Continue;
    private int m_phaseStart;
    private byte m_roleA = ScriptedTradeHarness.ConnectionNotEstablished;
    private byte m_roleB = ScriptedTradeHarness.ConnectionNotEstablished;
    private bool m_rolesSeen;
    private bool m_reachedTradeCenter;

    private ScriptedTradeDriver(byte[] rom, bool attemptTrade) {
        m_rom = rom;
        m_attemptTrade = attemptTrade;
        m_a = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideA);
        m_b = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideB);
    }

    private TradeResult Drive(int churnAtStep, List<TradeProbe>? probes, Action<TradeFrame>? onFrame) {
        var continueScript = ScriptedTradeHarness.ContinueScript();
        var expectedLeadA = TradeSaveFactory.ReadLeadSpecies(sram: TradeSaveFactory.CreateSram(trainer: TradeSaveFactory.SideA));
        var expectedLeadB = TradeSaveFactory.ReadLeadSpecies(sram: TradeSaveFactory.CreateSram(trainer: TradeSaveFactory.SideB));

        Observe(instance: m_a, tally: m_tallyA);
        Observe(instance: m_b, tally: m_tallyB);

        // Required symmetry break: two identical Cgb machines connected at identical post-boot state, and
        // then pair-stepped to equal cumulative CycleCount by the SerialLinkSession, have IDENTICAL free-running DIV
        // counters for the whole run — so WaitForLinkedFriend's rDIV-jitter (each side spins on DIV bit 7 before asserting
        // its clock role) breaks the two sides identically and they livelock, both landing on USING_EXTERNAL_CLOCK, never
        // one master + one slave. Advancing side B by half a DIV bit-7 period (32768 T-cycles) BEFORE connecting gives it a
        // persistent DIV offset the session preserves (it re-anchors targets at connect, so the head start carries), so the
        // two sides read opposite DIV bit 7 at the jitter check and the rendezvous resolves to exactly one $01 and one $02.
        // A frame stagger alone does NOT work: the session balances CycleCount, erasing any frame-level lead.
        m_b.Machine.Run(tCycles: RendezvousDivOffsetCycles);

        var session = new SerialLinkSession(first: m_a, second: m_b);

        try {
            for (var frame = 0; ((m_phase != Phase.Done) && (m_phase != Phase.Failed)); ++frame) {
                var localFrame = (frame - m_phaseStart);

                probes?.Add(item: new TradeProbe(Phase: m_phase, Idle: IsIdle(), Completed: m_tallyA.Completions));

                if (frame == churnAtStep) {
                    if (!IsIdle()) {
                        throw new InvalidOperationException(message: $"the churn boundary at frame {frame} is not transfer-idle on both ports.");
                    }

                    session = Churn(session: session);
                }

                var (buttonsA, buttonsB) = Inputs(frame: frame, localFrame: localFrame, continueScript: continueScript);

                m_a.GetRequiredService<IJoypad>().SetButtons(pressed: buttonsA);
                m_b.GetRequiredService<IJoypad>().SetButtons(pressed: buttonsB);
                session.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);

                CaptureRoles();
                onFrame?.Invoke(obj: new TradeFrame(Frame: frame, Phase: m_phase, LocalFrame: localFrame, ButtonsA: buttonsA, ButtonsB: buttonsB, Driver: this));
                Advance(frame: frame, localFrame: localFrame, expectedLeadA: expectedLeadA, expectedLeadB: expectedLeadB);
            }

            return new TradeResult(
                Completed: (m_phase == Phase.Done),
                ReachedTradeCenter: m_reachedTradeCenter,
                LinkModeA: ScriptedTradeHarness.Peek(machine: m_a, address: ScriptedTradeHarness.LinkModeAddress),
                LinkModeB: ScriptedTradeHarness.Peek(machine: m_b, address: ScriptedTradeHarness.LinkModeAddress),
                RolesResolved: (m_rolesSeen && (((m_roleA == ScriptedTradeHarness.UsingExternalClock) && (m_roleB == ScriptedTradeHarness.UsingInternalClock)) || ((m_roleA == ScriptedTradeHarness.UsingInternalClock) && (m_roleB == ScriptedTradeHarness.UsingExternalClock)))),
                RoleA: m_roleA,
                RoleB: m_roleB,
                LeadA: TradeSaveFactory.ReadLeadSpecies(sram: ScriptedTradeHarness.ExportSram(machine: m_a)),
                LeadB: TradeSaveFactory.ReadLeadSpecies(sram: ScriptedTradeHarness.ExportSram(machine: m_b)),
                ChecksumOkA: TradeSaveFactory.VerifyChecksum(sram: ScriptedTradeHarness.ExportSram(machine: m_a)),
                ChecksumOkB: TradeSaveFactory.VerifyChecksum(sram: ScriptedTradeHarness.ExportSram(machine: m_b)),
                TrafficA: m_tallyA.ToTraffic(),
                TrafficB: m_tallyB.ToTraffic(),
                StateA: m_a.Machine.Snapshot(),
                StateB: m_b.Machine.Snapshot(),
                SramA: ScriptedTradeHarness.ExportSram(machine: m_a),
                SramB: ScriptedTradeHarness.ExportSram(machine: m_b)
            );
        } finally {
            session.Dispose();
            m_a.Dispose();
            m_b.Dispose();
        }
    }

    // Suspend at a transfer-idle instant, snapshot both machines, restore into fresh machines, reconnect with the token —
    // the credit-preserving reconnect. Phase state is host-side (this driver), so it survives the swap; the
    // traffic tallies survive by being re-attached to the fresh ports around the same accumulators.
    private SerialLinkSession Churn(SerialLinkSession session) {
        var token = session.Suspend();
        var stateA = m_a.Machine.Snapshot();
        var stateB = m_b.Machine.Snapshot();
        var freshA = ScriptedTradeHarness.Build(rom: m_rom, trainer: TradeSaveFactory.SideA);
        var freshB = ScriptedTradeHarness.Build(rom: m_rom, trainer: TradeSaveFactory.SideB);

        freshA.Machine.Restore(snapshot: stateA);
        freshB.Machine.Restore(snapshot: stateB);
        m_a.Dispose();
        m_b.Dispose();
        m_a = freshA;
        m_b = freshB;
        Observe(instance: m_a, tally: m_tallyA);
        Observe(instance: m_b, tally: m_tallyB);

        return new SerialLinkSession(first: m_a, second: m_b, resumeToken: token);
    }

    // The per-side held-button state for a frame, dispatched on the current phase. Directional turns are single-press
    // windows; dialogue/menu advances are edge-mashed; the receptionist rendezvous staggers side B behind side A.
    private (JoypadButtons A, JoypadButtons B) Inputs(int frame, int localFrame, LinkInputScript continueScript) =>
        m_phase switch {
            Phase.Continue => (continueScript.ButtonsAt(frame: frame), continueScript.ButtonsAt(frame: frame)),
            // Tap UP briefly (the first two local frames) to TURN toward the attendant, then release — a held direction
            // walks/bumps; a tap turns in place. The overworld must have settled first (see the Continue transition).
            Phase.FaceUp => (FaceTap(localFrame: localFrame), FaceTap(localFrame: localFrame)),
            Phase.Receptionist => (MashA(localFrame: localFrame), MashA(localFrame: (localFrame - RendezvousStaggerFrames))),
            // Each side walks onto its vacated-CHRIS seat tile on row Y=4 and turns to the bg_event's required facing
            // (side A: (6,4) facing LEFT toward console (5,4); side B: (3,4) facing RIGHT toward console (4,4)). Facing
            // any other way does NOT fire the console — a directional bg_event only matches its own facing. The A-press
            // that fires `special TradeCenter` is the Console phase's mash, once BOTH sides are seated.
            Phase.Approach => (ApproachSide(machine: m_a, seatX: SideASeatX, wantDir: FacingLeft, localFrame: localFrame), ApproachSide(machine: m_b, seatX: SideBSeatX, wantDir: FacingRight, localFrame: localFrame)),
            // Seated and facing the console: mash A (only) until this side's script engine reports the console script
            // running — the press that lands after the turn animation settles fires `special TradeCenter`.
            Phase.Console => (ConsoleSide(machine: m_a, localFrame: localFrame), ConsoleSide(machine: m_b, localFrame: localFrame)),
            // `special TradeCenter` runs the block exchange AND the party-selection / confirm UI internally. Plain A-mash
            // does NOT trade: pressing A on a party mon opens a horizontal STATS|TRADE submenu with the cursor on STATS, so
            // A there just opens the stats screen and loops (the cart's link menu loop). The trade is
            // reached only by RIGHT (cursor -> TRADE) then A. This cyclic A -> RIGHT -> A pattern
            // drives: party-menu A (open submenu) -> RIGHT (to TRADE) -> A (select) -> and the "mon for mon TRADE/CANCEL?"
            // confirm popup (cursor defaults to TRADE) -> A. It cycles so it is robust to the block-exchange + partner-sync
            // waits (presses on a not-yet-ready menu are no-ops).
            Phase.TradeMenu => (TradeMenu(localFrame: localFrame), TradeMenu(localFrame: localFrame)),
            Phase.Cancel => (CancelDrive(localFrame: localFrame), CancelDrive(localFrame: localFrame)),
            _ => (JoypadButtons.None, JoypadButtons.None),
        };

    // Evaluates the current phase's peek condition and either advances to the next phase or fails when its frame ceiling
    // is blown without the condition being met.
    private void Advance(int frame, int localFrame, byte expectedLeadA, byte expectedLeadB) {
        switch (m_phase) {
            case Phase.Continue:
                // Wait for BOTH the map to load AND the overworld to fully settle (fade-in complete, input accepted) —
                // transitioning the instant the map loads leaves the player unable to turn (input still queued/ignored).
                if (ScriptedTradeHarness.IsAtCableClubFloor(machine: m_a) && ScriptedTradeHarness.IsAtCableClubFloor(machine: m_b) && (frame >= ContinueSettleFrame)) {
                    Transition(next: Phase.FaceUp, frame: frame);
                } else if (localFrame >= ContinueBudget) {
                    m_phase = Phase.Failed;
                }

                break;
            case Phase.FaceUp:
                // Turned to face the attendant (wPlayerDirection UP = 4) — proceed to mash A through the dialogue.
                if ((ScriptedTradeHarness.Peek(machine: m_a, address: ScriptedTradeHarness.PlayerDirectionAddress) == PlayerDirectionUp)
                    && (ScriptedTradeHarness.Peek(machine: m_b, address: ScriptedTradeHarness.PlayerDirectionAddress) == PlayerDirectionUp)) {
                    Transition(next: Phase.Receptionist, frame: frame);
                } else if (localFrame >= FaceUpBudget) {
                    m_phase = Phase.Failed;
                }

                break;
            case Phase.Receptionist:
                // Both players warped into TRADE_CENTER with the link established (wLinkMode = LINK_TRADECENTER) — the
                // observable established-link condition. The non-trade path stops here; the trade path continues into
                // the console navigation + mon-selection trade.
                if (AtTradeCenter(machine: m_a) && AtTradeCenter(machine: m_b)
                    && (ScriptedTradeHarness.Peek(machine: m_a, address: ScriptedTradeHarness.LinkModeAddress) == LinkTradeCenter)
                    && (ScriptedTradeHarness.Peek(machine: m_b, address: ScriptedTradeHarness.LinkModeAddress) == LinkTradeCenter)) {
                    m_reachedTradeCenter = true;

                    if (m_attemptTrade) {
                        Transition(next: Phase.Approach, frame: frame);
                    } else {
                        m_phase = Phase.Done;
                    }
                } else if (localFrame >= ReceptionistBudget) {
                    m_phase = Phase.Failed;
                }

                break;
            case Phase.Approach:
                // Both players seated at their own console facing it — the Console phase's A-mash can now fire
                // `special TradeCenter`.
                if (Seated(machine: m_a, seatX: SideASeatX, wantDir: FacingLeft) && Seated(machine: m_b, seatX: SideBSeatX, wantDir: FacingRight)) {
                    Transition(next: Phase.Console, frame: frame);
                } else if (localFrame >= ApproachBudget) {
                    m_phase = Phase.Failed;
                }

                break;
            case Phase.Console:
                // Both consoles fired: each side is inside `special TradeCenter` (sprite updates disabled for the whole
                // trade UI) — the menu drive can begin.
                if (TradeUiOpen(machine: m_a) && TradeUiOpen(machine: m_b)) {
                    Transition(next: Phase.TradeMenu, frame: frame);
                } else if (localFrame >= ConsoleBudget) {
                    m_phase = Phase.Failed;
                }

                break;
            case Phase.TradeMenu:
                // The whole trade — block exchange, mon offer/confirm, animation, and the auto-save that swaps the leads —
                // runs inside `special TradeCenter`; the observable that it committed is each side's exported SRAM lead
                // species becoming the OTHER side's original (the auto-save writes it before the loop-back).
                if (TradeCommitted(expectedLeadA: expectedLeadA, expectedLeadB: expectedLeadB)) {
                    Transition(next: Phase.Cancel, frame: frame);
                } else if (localFrame >= TradeMenuBudget) {
                    m_phase = Phase.Failed;
                }

                break;
            case Phase.Cancel:
                // Out of the trade UI on both sides: the $F/$F cancel handshake ran ExitLinkCommunications and the
                // post-link map reload re-enabled sprite updates, landing both players back in the TRADE_CENTER
                // overworld. hSerialConnectionStatus deliberately STAYS $01/$02 here — the game only resets it to $FF
                // (Link_ResetSerialRegistersAfterLinkClosure) when the player walks out through the Pokécenter door, so
                // waiting for $FF inside the room would never terminate.
                if (!TradeUiOpen(machine: m_a) && !TradeUiOpen(machine: m_b) && AtTradeCenter(machine: m_a) && AtTradeCenter(machine: m_b)) {
                    m_phase = Phase.Done;
                } else if (localFrame >= CancelBudget) {
                    m_phase = Phase.Failed;
                }

                break;
            default:
                break;
        }
    }
    private void Transition(Phase next, int frame) {
        m_phase = next;
        m_phaseStart = (frame + 1);
    }

    // The rendezvous roles once WaitForLinkedFriend resolves: $01 on exactly one machine, $02 on the other. Captured the
    // first frame a valid (external, internal) pair appears and held (later phases overwrite $FFCD).
    private void CaptureRoles() {
        if (m_rolesSeen) {
            return;
        }

        var a = ScriptedTradeHarness.ConnectionStatus(machine: m_a);
        var b = ScriptedTradeHarness.ConnectionStatus(machine: m_b);

        if (((a == ScriptedTradeHarness.UsingExternalClock) && (b == ScriptedTradeHarness.UsingInternalClock))
            || ((a == ScriptedTradeHarness.UsingInternalClock) && (b == ScriptedTradeHarness.UsingExternalClock))) {
            m_roleA = a;
            m_roleB = b;
            m_rolesSeen = true;
        }
    }
    private bool TradeCommitted(byte expectedLeadA, byte expectedLeadB) {
        var leadA = TradeSaveFactory.ReadLeadSpecies(sram: ScriptedTradeHarness.ExportSram(machine: m_a));
        var leadB = TradeSaveFactory.ReadLeadSpecies(sram: ScriptedTradeHarness.ExportSram(machine: m_b));

        return ((leadA == expectedLeadB) && (leadB == expectedLeadA));
    }
    private static bool AtTradeCenter(MachineInstance machine) =>
        ((ScriptedTradeHarness.LiveMapGroup(machine: machine) == TradeCenterGroup) && (ScriptedTradeHarness.LiveMapNumber(machine: machine) == TradeCenterMap));
    private bool IsIdle() =>
        (((m_a.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress) & 0x80) == 0)
            && ((m_b.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress) & 0x80) == 0));

    // Tap UP on the first two local frames of the FaceUp phase, then release — a brief tap turns in place toward the
    // attendant (a held direction would walk or bump-shuffle).
    private static JoypadButtons FaceTap(int localFrame) =>
        ((localFrame < 2) ? JoypadButtons.Up : JoypadButtons.None);
    private static JoypadButtons MashA(int localFrame) =>
        (((localFrame >= 0) && ((localFrame % MashPeriod) < MashPress)) ? JoypadButtons.A : JoypadButtons.None);

    // One cycle of the trade-menu drive: A (0..2) -> settle -> RIGHT (30..32) -> settle -> A (45..47) -> long settle.
    private static JoypadButtons TradeMenu(int localFrame) {
        var phase = (localFrame % TradeMenuCycle);

        return phase switch {
            >= 0 and < 3 => JoypadButtons.A,
            >= 30 and < 33 => JoypadButtons.Right,
            >= 45 and < 48 => JoypadButtons.A,
            _ => JoypadButtons.None,
        };
    }

    // One cycle of the cancel drive: DOWN (0..2) -> settle -> DOWN (20..22) -> settle -> A (40..42) -> long settle.
    private static JoypadButtons CancelDrive(int localFrame) {
        var phase = (localFrame % CancelCycle);

        return phase switch {
            >= 0 and < 3 => JoypadButtons.Down,
            >= 20 and < 23 => JoypadButtons.Down,
            >= 40 and < 43 => JoypadButtons.A,
            _ => JoypadButtons.None,
        };
    }

    // Peek-gated navigation to one side's trade console seat: climb off the exit-warp row to the corridor, align to the
    // seat column, step straight up onto the seat tile (Y=4), then tap-turn to the required facing. Per-machine peeks
    // make it robust to the entry position and step timing; the console A-press belongs to the Console phase. This walk
    // also depends on VRAM reads unlocking with the mode-0 STAT transition at dot offset +4 and on the crafted save
    // initializing wObjectFollow_* to $FF (see TradeSaveFactory.OffsetObjectFollowLeader).
    private static JoypadButtons ApproachSide(MachineInstance machine, byte seatX, byte wantDir, int localFrame) {
        var x = ScriptedTradeHarness.Peek(machine: machine, address: WXCoordAddress);
        var y = ScriptedTradeHarness.Peek(machine: machine, address: WYCoordAddress);
        var dir = ScriptedTradeHarness.Peek(machine: machine, address: ScriptedTradeHarness.PlayerDirectionAddress);

        // Climb off the entry / exit-warp row (Y=7) FIRST, to an open corridor row (Y=5), before shifting columns —
        // walking along Y=7 re-triggers the TRADE_CENTER exit warp (maps/TradeCenter.asm (4,7)/(5,7)).
        if (y > CorridorY) {
            return JoypadButtons.Up;
        }

        // Align to the seat column at the corridor row, THEN step straight up onto the seat tile.
        if (y > SeatY) {
            if (x < seatX) {
                return JoypadButtons.Right;
            }

            if (x > seatX) {
                return JoypadButtons.Left;
            }

            return JoypadButtons.Up;
        }

        if (x < seatX) {
            return JoypadButtons.Right;
        }

        if (x > seatX) {
            return JoypadButtons.Left;
        }

        if (dir != wantDir) {
            // TAP the turn (an edge, then release), never a hold: a held direction into the solid console furniture
            // walks/bumps every frame (a continuous bump-shuffle animation), whereas a clean tap turns in place toward
            // the console without a bump. Mirrors FaceUp's FaceTap.
            var turn = ((wantDir == FacingLeft) ? JoypadButtons.Left : JoypadButtons.Right);

            return (((localFrame % MashPeriod) < MashPress) ? turn : JoypadButtons.None);
        }

        // Seated and facing the console: HOLD (no A). The A-press that fires `special TradeCenter` is deferred to the
        // Console phase, which the driver enters only once BOTH sides are seated — so neither side steps into the trade
        // special while the other is still walking (a one-sided special desyncs the link).
        return JoypadButtons.None;
    }

    // Mash A until this side's console script fires (a press mid-turn-animation is eaten; once the trade UI opens,
    // further presses stop so the menu drive begins from a quiet pad).
    private static JoypadButtons ConsoleSide(MachineInstance machine, int localFrame) =>
        (TradeUiOpen(machine: machine) ? JoypadButtons.None : MashA(localFrame: localFrame));

    // `special TradeCenter` disables overworld sprite updates for its whole run — the discriminator that the console
    // fired and the trade UI owns the machine.
    private static bool TradeUiOpen(MachineInstance machine) =>
        (ScriptedTradeHarness.Peek(machine: machine, address: ScriptedTradeHarness.SpriteUpdatesEnabledAddress) == 0);
    private bool Seated(MachineInstance machine, byte seatX, byte wantDir) =>
        ((ScriptedTradeHarness.Peek(machine: machine, address: WXCoordAddress) == seatX)
            && (ScriptedTradeHarness.Peek(machine: machine, address: WYCoordAddress) == SeatY)
            && (ScriptedTradeHarness.Peek(machine: machine, address: ScriptedTradeHarness.PlayerDirectionAddress) == wantDir));
    private static void Observe(MachineInstance instance, TrafficTally tally) {
        var port = instance.GetRequiredService<SerialComponent>();

        port.ByteTransmitted = tally.OnSend;
        port.TransferCompleted = tally.OnComplete;
    }

    /// <summary>The current side-A machine (explorer diagnostics only).</summary>
    public MachineInstance MachineA =>
        m_a;

    /// <summary>The current side-B machine (explorer diagnostics only).</summary>
    public MachineInstance MachineB =>
        m_b;

    private enum Phase {
        Continue,
        FaceUp,
        Receptionist,
        Approach,
        Console,
        TradeMenu,
        Cancel,
        Done,
        Failed,
    }

    // A host-side, never-serialized tally of one port's serial traffic (the LinkReplay fingerprint idiom): internal-clock
    // sends, every completion, and an FNV-1a fold of each completed byte. A mutable class so the same accumulators survive
    // being re-attached to a fresh port after a churn.
    private sealed class TrafficTally {
        private const ulong FnvOffsetBasis = 0xCBF29CE484222325ul;
        private const ulong FnvPrime = 0x100000001B3ul;

        public int Completions;
        public ulong Hash = FnvOffsetBasis;
        public int MasterSends;

        public void OnComplete(byte value) {
            ++Completions;
            Hash = ((Hash ^ value) * FnvPrime);
        }
        public void OnSend(byte value) =>
            ++MasterSends;
        public LinkSideTraffic ToTraffic() =>
            new(MasterSends: MasterSends, Completions: Completions, TrafficHash: Hash);
    }
}

/// <summary>A per-frame observation the trade explorer logs: the driver's phase, the buttons applied, and the driver so a
/// caller can peek the live machines.</summary>
internal readonly record struct TradeFrame(
    int Frame,
    object Phase,
    int LocalFrame,
    JoypadButtons ButtonsA,
    JoypadButtons ButtonsB,
    ScriptedTradeDriver Driver
);

/// <summary>A per-frame idle/phase probe the churn-step picker walks (mirrors <c>LinkChurnStage</c>'s boundary probes).</summary>
internal readonly record struct TradeProbe(object Phase, bool Idle, int Completed);

/// <summary>The outcome of one scripted-trade run: whether it completed, the resolved rendezvous roles, both sides' final
/// lead species + checksum validity, both serial-traffic fingerprints, and both final whole-machine snapshots and SRAMs.</summary>
internal readonly record struct TradeResult(
    bool Completed,
    bool ReachedTradeCenter,
    byte LinkModeA,
    byte LinkModeB,
    bool RolesResolved,
    byte RoleA,
    byte RoleB,
    byte LeadA,
    byte LeadB,
    bool ChecksumOkA,
    bool ChecksumOkB,
    LinkSideTraffic TrafficA,
    LinkSideTraffic TrafficB,
    MachineSnapshot StateA,
    MachineSnapshot StateB,
    byte[] SramA,
    byte[] SramB
);
