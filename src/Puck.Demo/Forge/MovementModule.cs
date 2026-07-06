namespace Puck.Demo.Forge;

/// <summary>
/// The overworld walker's direction-lock: how held d-pad bits turn into a per-frame pixel step + facing. Lives
/// beside <see cref="HgbCartridge"/> (not <c>Forge/Framework/</c>) because <see cref="HgbCartridge.BuildOverworld"/>'s
/// walker is a hand-emitted routine that predates the framework kernel — it shares no module with
/// <c>Forge/Framework/InputModule.cs</c>, so the direction table lives where the emission it feeds already lives.
/// </summary>
/// <remarks>
/// <para><see cref="MovementMode.FourWay"/> is the untouched historical path: four independent per-axis steps, one
/// per held cardinal bit — <see cref="HgbCartridge"/> emits it exactly as before (the byte-identical regression bar),
/// so this module does not touch that code at all.</para>
///
/// <para><see cref="MovementMode.EightWay"/> keeps the same per-axis independent stepping (a held diagonal — two
/// cardinal lines at once — moves BOTH axes by the same per-axis delta a lone cardinal press would use), which is the
/// classic brick-era "faster diagonal" artifact: a diagonal step covers √2× the distance of a cardinal one because
/// nothing scales the per-axis delta down to compensate. That is authentic period behavior, not a bug, and is why
/// FourWay and EightWay share one stepping shape — the only real fork is how FACING resolves on a diagonal (see
/// <see cref="EmitFacingResolve"/>).</para>
///
/// <para><see cref="MovementMode.Hex"/> is the one mode with a genuinely different step table, matching the pointy-top
/// convention the 3D side's hex walk grid uses (<c>WorldDocument.MovementLock</c>): Left/Right are the pure west/east
/// neighbors, the four diagonals are the remaining 60° neighbors, and pure Up/Down alone has NO neighbor to move to
/// (a pointy-top hex cell has no vertical edge) — so a lone Up or Down press holds position. The step deltas approximate
/// the 60°-apart unit vectors with small integers rather than true trigonometry (exact 60° needs an irrational
/// √3 factor, which has no exact fixed-point SM83 representation at this pixel scale): west/east move
/// (±<see cref="HexAxisStep"/>, 0) to match the cardinal <see cref="OverworldProtocol.WalkSpeed"/>, and each diagonal
/// moves (±<see cref="HexDiagonalXStep"/>, ±<see cref="HexDiagonalYStep"/>) — half the X reach of a pure horizontal
/// step, the same Y reach as a pure vertical one would have had. The 1:2 ratio reads as a recognizable 60°-ish
/// diagonal at brick resolution without needing anything beyond 8-bit adds.</para>
/// </remarks>
internal static class MovementModule {
    /// <summary>The west/east step (pixels per frame) in <see cref="MovementMode.Hex"/> — matches
    /// <see cref="OverworldProtocol.WalkSpeed"/> so the pure-horizontal case reads identically to the other modes.</summary>
    public const byte HexAxisStep = OverworldProtocol.WalkSpeed;

    /// <summary>The X component (pixels per frame) of a <see cref="MovementMode.Hex"/> diagonal step — half
    /// <see cref="HexAxisStep"/>, the rational approximation's short leg.</summary>
    public const byte HexDiagonalXStep = (HexAxisStep / 2);

    /// <summary>The Y component (pixels per frame) of a <see cref="MovementMode.Hex"/> diagonal step — the rational
    /// approximation's long leg, equal to <see cref="HexAxisStep"/>.</summary>
    public const byte HexDiagonalYStep = HexAxisStep;

    /// <summary>Emits one held-direction check: if the d-pad bit (held in <c>B</c>, active-low) is PRESSED, adds
    /// <paramref name="delta"/> to the byte at <paramref name="address"/> (subtracting instead when
    /// <paramref name="negative"/>) and marks the frame as moving — but does NOT touch facing (the caller resolves
    /// facing once, after every axis has had a chance to move, per <see cref="MovementMode"/>'s facing rule).</summary>
    /// <param name="emitter">The routine's emitter.</param>
    /// <param name="bit">The d-pad bit to test (0=Right, 1=Left, 2=Up, 3=Down).</param>
    /// <param name="address">The WRAM byte to step (<see cref="OverworldProtocol.PlayerXAddress"/> or
    /// <see cref="OverworldProtocol.PlayerYAddress"/>).</param>
    /// <param name="delta">The per-frame pixel step (0..127; the loop below only ever passes small constants).</param>
    /// <param name="negative">Whether the step subtracts instead of adding.</param>
    /// <param name="movingAddress">The WRAM moving-flag byte to raise on a match.</param>
    public static void EmitConditionalStep(Sm83Emitter emitter, int bit, ushort address, byte delta, bool negative, ushort movingAddress) {
        var skip = emitter.NewLabel();

        emitter.TestBit(register: Reg8.B, bit: bit);
        emitter.JumpRelative(condition: Condition.NotZero, label: skip); // bit set => not pressed => skip

        emitter.LoadAFromAddress(address: address);

        for (var step = 0; (step < delta); step++) {
            if (negative) {
                emitter.Decrement(register: Reg8.A);
            } else {
                emitter.Increment(register: Reg8.A);
            }
        }

        emitter.StoreAToAddress(address: address);
        emitter.LoadAImmediate(value: 1);
        emitter.StoreAToAddress(address: movingAddress);
        emitter.MarkLabel(label: skip);
    }

    /// <summary>Emits the facing resolution shared by <see cref="MovementMode.EightWay"/> and
    /// <see cref="MovementMode.Hex"/>: given the four held d-pad bits in <c>B</c>, sets the byte at
    /// <paramref name="facingAddress"/> using the documented rule — horizontal wins when |dx| &gt;= |dy|, otherwise
    /// vertical. Every step this module emits is equal magnitude per axis within a frame, so a HELD horizontal
    /// direction always ties-or-beats a held vertical one; the emission therefore checks Right/Left FIRST and only
    /// examines Up/Down when NEITHER horizontal bit is held (rather than last-bit-wins), which is what makes this
    /// different from <see cref="MovementMode.FourWay"/>'s facing order. No direction held leaves facing untouched
    /// (the idle pose keeps the last faced direction, matching <see cref="HgbCartridge"/>'s existing idle
    /// behaviour).</summary>
    /// <param name="emitter">The routine's emitter.</param>
    /// <param name="facingAddress">The WRAM facing byte.</param>
    /// <param name="facingRight">The facing id for right.</param>
    /// <param name="facingLeft">The facing id for left.</param>
    /// <param name="facingUp">The facing id for up.</param>
    /// <param name="facingDown">The facing id for down.</param>
    public static void EmitFacingResolve(Sm83Emitter emitter, ushort facingAddress, byte facingRight, byte facingLeft, byte facingUp, byte facingDown) {
        var rightHeld = emitter.NewLabel();
        var leftHeld = emitter.NewLabel();
        var checkVertical = emitter.NewLabel();
        var done = emitter.NewLabel();

        emitter.TestBit(register: Reg8.B, bit: 0); // Right
        emitter.JumpRelative(condition: Condition.Zero, label: rightHeld);
        emitter.TestBit(register: Reg8.B, bit: 1); // Left
        emitter.JumpRelative(condition: Condition.Zero, label: leftHeld);
        emitter.JumpRelative(label: checkVertical); // neither horizontal bit held

        emitter.MarkLabel(label: rightHeld);
        emitter.LoadAImmediate(value: facingRight);
        emitter.StoreAToAddress(address: facingAddress);
        emitter.JumpRelative(label: done);

        emitter.MarkLabel(label: leftHeld);
        emitter.LoadAImmediate(value: facingLeft);
        emitter.StoreAToAddress(address: facingAddress);
        emitter.JumpRelative(label: done);

        emitter.MarkLabel(label: checkVertical);
        EmitFacingIfHeld(emitter: emitter, bit: 2, facingAddress: facingAddress, facing: facingUp);
        EmitFacingIfHeld(emitter: emitter, bit: 3, facingAddress: facingAddress, facing: facingDown);
        emitter.MarkLabel(label: done);
    }

    private static void EmitFacingIfHeld(Sm83Emitter emitter, int bit, ushort facingAddress, byte facing) {
        var skip = emitter.NewLabel();

        emitter.TestBit(register: Reg8.B, bit: bit);
        emitter.JumpRelative(condition: Condition.NotZero, label: skip); // bit set => not pressed => skip
        emitter.LoadAImmediate(value: facing);
        emitter.StoreAToAddress(address: facingAddress);
        emitter.MarkLabel(label: skip);
    }
}

/// <summary>The overworld walker's direction lock. See <see cref="MovementModule"/> for the per-mode step tables and
/// the facing-resolution rule; <c>free</c> in the 3D-side <c>WorldDocument.MovementLock</c> has no brick analogue
/// (the walker is always direction-locked to one of these three), so <see cref="FourWay"/> is the brick default.</summary>
internal enum MovementMode {
    /// <summary>Today's historical behaviour: four independent per-axis steps, facing resolved last-held-wins in
    /// Right/Left/Up/Down order. The BYTE-IDENTICAL default — never changes emitted code from before this mode
    /// existed.</summary>
    FourWay = 0,

    /// <summary>Same per-axis independent stepping as <see cref="FourWay"/> (a diagonal moves both axes at the full
    /// cardinal delta — the classic faster-diagonal artifact), but facing resolves via
    /// <see cref="MovementModule.EmitFacingResolve"/>'s horizontal-wins-ties rule instead of last-bit-wins.</summary>
    EightWay = 1,

    /// <summary>The pointy-top hex lock: Left/Right are the pure west/east neighbors; Up+Left, Up+Right, Down+Left,
    /// Down+Right are the four 60° neighbors; a lone Up or Down has no neighbor and does not move. See
    /// <see cref="MovementModule"/>'s remarks for the step-delta derivation.</summary>
    Hex = 2,
}
