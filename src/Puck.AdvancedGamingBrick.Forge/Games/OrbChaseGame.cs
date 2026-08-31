namespace Puck.AdvancedGamingBrick.Forge.Games;

/// <summary>
/// Orb Chase's Thumb routine: a title state (blue bar; START seeds the PRNG from the frame counter and commits to
/// play), then a play state where the green player square steps 8 pixels per D-pad pressed edge, the PRNG places a
/// red orb on the 30×20 grid, and landing on the orb scores and re-places it (never onto the player's cell).
/// Register discipline per routine is noted at each emission site; every game field lives in
/// <see cref="OrbChaseProtocol"/>'s EWRAM slots.
/// </summary>
internal static class OrbChaseGame {
    private sealed class Labels {
        public int DrawPlayer;
        public int DrawTarget;
        public int MainLoop;
        public int PlaceTarget;
        public int PlayTick;
        public int TitleTick;
    }

    /// <summary>Assembles the routine (base address <see cref="AgbForgeCartridge.CodeAddress"/>).</summary>
    /// <returns>The Thumb machine code.</returns>
    public static byte[] Assemble() {
        var emitter = new ThumbEmitter();
        var kernel = new AgbForgeKernel(emitter: emitter);
        var labels = new Labels {
            DrawPlayer = emitter.NewLabel(),
            DrawTarget = emitter.NewLabel(),
            MainLoop = emitter.NewLabel(),
            PlaceTarget = emitter.NewLabel(),
            PlayTick = emitter.NewLabel(),
            TitleTick = emitter.NewLabel(),
        };

        EmitBoot(emitter: emitter, kernel: kernel);
        EmitMainLoop(emitter: emitter, kernel: kernel, labels: labels);
        EmitTitleTick(emitter: emitter, kernel: kernel, labels: labels);
        EmitPlayTick(emitter: emitter, kernel: kernel, labels: labels);
        EmitPlaceTarget(emitter: emitter, kernel: kernel, labels: labels);
        EmitDrawSquare(colour: OrbChaseProtocol.ColourPlayer, emitter: emitter, kernel: kernel, label: labels.DrawPlayer, xOffset: OrbChaseProtocol.PlayerXOffset, yOffset: OrbChaseProtocol.PlayerYOffset);
        EmitDrawSquare(colour: OrbChaseProtocol.ColourTarget, emitter: emitter, kernel: kernel, label: labels.DrawTarget, xOffset: OrbChaseProtocol.TargetXOffset, yOffset: OrbChaseProtocol.TargetYOffset);
        kernel.EmitLibrary();

        return emitter.ToArray(baseAddress: AgbForgeCartridge.CodeAddress);
    }

    // Boot: kernel prologue (state clear, mode 3), full-screen clear, title bar. Falls into the main loop.
    private static void EmitBoot(ThumbEmitter emitter, AgbForgeKernel kernel) {
        kernel.EmitBootPrologue();
        EmitFillScreenCall(colour: OrbChaseProtocol.ColourBackground, emitter: emitter, kernel: kernel);

        emitter.MoveImmediate(destination: LowRegister.R0, value: 0);
        emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
        emitter.MoveImmediate(destination: LowRegister.R2, value: ((byte)AgbHw.ScreenWidth));
        emitter.MoveImmediate(destination: LowRegister.R3, value: OrbChaseProtocol.TitleBarHeight);
        emitter.LoadConstant(destination: LowRegister.R4, value: OrbChaseProtocol.ColourTitleBar);
        kernel.EmitFillRectCall();
    }
    private static void EmitMainLoop(ThumbEmitter emitter, AgbForgeKernel kernel, Labels labels) {
        var playBranch = emitter.NewLabel();

        emitter.MarkLabel(label: labels.MainLoop);
        kernel.EmitFrameSyncCall();
        emitter.LoadConstant(destination: LowRegister.R0, value: AgbForgeMemoryMap.StateBase);
        emitter.LoadHalf(baseRegister: LowRegister.R0, byteOffset: AgbForgeMemoryMap.GameStateOffset, destination: LowRegister.R1);
        emitter.CompareImmediate(register: LowRegister.R1, value: ((byte)OrbChaseProtocol.StatePlay));
        emitter.Branch(condition: ThumbCondition.Equal, label: playBranch);
        emitter.Call(label: labels.TitleTick);
        emitter.Branch(label: labels.MainLoop);
        emitter.MarkLabel(label: playBranch);
        emitter.Call(label: labels.PlayTick);
        emitter.Branch(label: labels.MainLoop);
    }
    // titleTick: on the START pressed edge — seed the PRNG, commit to play, spawn the player, clear the screen,
    // place and draw the orb and player.
    private static void EmitTitleTick(ThumbEmitter emitter, AgbForgeKernel kernel, Labels labels) {
        var done = emitter.NewLabel();

        emitter.MarkLabel(label: labels.TitleTick);
        emitter.Push(includeLinkRegister: true, registers: LowRegisterMask.None);
        emitter.LoadConstant(destination: LowRegister.R0, value: AgbForgeMemoryMap.StateBase);
        emitter.LoadHalf(baseRegister: LowRegister.R0, byteOffset: AgbForgeMemoryMap.InputPressedOffset, destination: LowRegister.R1);
        emitter.MoveImmediate(destination: LowRegister.R2, value: ((byte)AgbKeys.Start));
        emitter.Alu(destination: LowRegister.R1, op: ThumbAlu.Test, source: LowRegister.R2);
        emitter.Branch(condition: ThumbCondition.Equal, label: done);

        kernel.EmitPrngSeedFromFrameCounter();

        emitter.LoadConstant(destination: LowRegister.R0, value: AgbForgeMemoryMap.StateBase);
        emitter.MoveImmediate(destination: LowRegister.R1, value: ((byte)OrbChaseProtocol.StatePlay));
        emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: AgbForgeMemoryMap.GameStateOffset, source: LowRegister.R1);

        emitter.LoadConstant(destination: LowRegister.R0, value: AgbForgeMemoryMap.GameRam);
        emitter.MoveImmediate(destination: LowRegister.R1, value: OrbChaseProtocol.PlayerStartX);
        emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: OrbChaseProtocol.PlayerXOffset, source: LowRegister.R1);
        emitter.MoveImmediate(destination: LowRegister.R1, value: OrbChaseProtocol.PlayerStartY);
        emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: OrbChaseProtocol.PlayerYOffset, source: LowRegister.R1);
        emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
        emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: OrbChaseProtocol.ScoreOffset, source: LowRegister.R1);

        EmitFillScreenCall(colour: OrbChaseProtocol.ColourBackground, emitter: emitter, kernel: kernel);
        emitter.Call(label: labels.PlaceTarget);
        emitter.Call(label: labels.DrawTarget);
        emitter.Call(label: labels.DrawPlayer);

        emitter.MarkLabel(label: done);
        emitter.Pop(includeProgramCounter: true, registers: LowRegisterMask.None);
    }
    // playTick: r5 holds the pressed halfword across the erase call (fillRect preserves r4–r7).
    private static void EmitPlayTick(ThumbEmitter emitter, AgbForgeKernel kernel, Labels labels) {
        var noHit = emitter.NewLabel();
        var noMove = emitter.NewLabel();

        emitter.MarkLabel(label: labels.PlayTick);
        emitter.Push(includeLinkRegister: true, registers: LowRegisterMask.R4 | LowRegisterMask.R5);
        emitter.LoadConstant(destination: LowRegister.R0, value: AgbForgeMemoryMap.StateBase);
        emitter.LoadHalf(baseRegister: LowRegister.R0, byteOffset: AgbForgeMemoryMap.InputPressedOffset, destination: LowRegister.R5);
        emitter.MoveImmediate(destination: LowRegister.R0, value: ((byte)(AgbKeys.Right | AgbKeys.Left | AgbKeys.Up | AgbKeys.Down)));
        emitter.Alu(destination: LowRegister.R5, op: ThumbAlu.Test, source: LowRegister.R0);
        emitter.Branch(condition: ThumbCondition.Equal, label: noMove);

        EmitErasePlayer(emitter: emitter, kernel: kernel);
        EmitApplyMovement(emitter: emitter);

        emitter.MarkLabel(label: noMove);
        emitter.LoadConstant(destination: LowRegister.R3, value: AgbForgeMemoryMap.GameRam);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.PlayerXOffset, destination: LowRegister.R0);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.TargetXOffset, destination: LowRegister.R1);
        emitter.Alu(destination: LowRegister.R0, op: ThumbAlu.Compare, source: LowRegister.R1);
        emitter.Branch(condition: ThumbCondition.NotEqual, label: noHit);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.PlayerYOffset, destination: LowRegister.R0);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.TargetYOffset, destination: LowRegister.R1);
        emitter.Alu(destination: LowRegister.R0, op: ThumbAlu.Compare, source: LowRegister.R1);
        emitter.Branch(condition: ThumbCondition.NotEqual, label: noHit);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.ScoreOffset, destination: LowRegister.R0);
        emitter.AddImmediate(register: LowRegister.R0, value: 1);
        emitter.StoreHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.ScoreOffset, source: LowRegister.R0);
        emitter.Call(label: labels.PlaceTarget);

        emitter.MarkLabel(label: noHit);
        emitter.Call(label: labels.DrawTarget);
        emitter.Call(label: labels.DrawPlayer);
        emitter.Pop(includeProgramCounter: true, registers: LowRegisterMask.R4 | LowRegisterMask.R5);
    }
    // Erase the player's current cell to the backdrop before its position changes.
    private static void EmitErasePlayer(ThumbEmitter emitter, AgbForgeKernel kernel) {
        emitter.LoadConstant(destination: LowRegister.R3, value: AgbForgeMemoryMap.GameRam);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.PlayerXOffset, destination: LowRegister.R0);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.PlayerYOffset, destination: LowRegister.R1);
        emitter.MoveImmediate(destination: LowRegister.R2, value: OrbChaseProtocol.SquareSize);
        emitter.MoveImmediate(destination: LowRegister.R3, value: OrbChaseProtocol.SquareSize);
        emitter.MoveImmediate(destination: LowRegister.R4, value: 0);
        kernel.EmitFillRectCall();
    }
    // Movement: one 8-pixel step per pressed edge per direction, clamped to the grid. r5 = pressed edges;
    // r0/r1 = player x/y; r3 = the game-RAM base for the final store.
    private static void EmitApplyMovement(ThumbEmitter emitter) {
        emitter.LoadConstant(destination: LowRegister.R3, value: AgbForgeMemoryMap.GameRam);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.PlayerXOffset, destination: LowRegister.R0);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.PlayerYOffset, destination: LowRegister.R1);

        EmitStep(axis: LowRegister.R0, emitter: emitter, increases: true, key: AgbKeys.Right, limit: OrbChaseProtocol.MaxX);
        EmitStep(axis: LowRegister.R0, emitter: emitter, increases: false, key: AgbKeys.Left, limit: OrbChaseProtocol.SquareSize);
        EmitStep(axis: LowRegister.R1, emitter: emitter, increases: false, key: AgbKeys.Up, limit: OrbChaseProtocol.SquareSize);
        EmitStep(axis: LowRegister.R1, emitter: emitter, increases: true, key: AgbKeys.Down, limit: OrbChaseProtocol.MaxY);

        emitter.StoreHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.PlayerXOffset, source: LowRegister.R0);
        emitter.StoreHalf(baseRegister: LowRegister.R3, byteOffset: OrbChaseProtocol.PlayerYOffset, source: LowRegister.R1);
    }
    // One direction: skip unless the key's edge is set; clamp (>= limit blocks a growing step, < limit blocks a
    // shrinking one); step by the square size. Clobbers r2.
    private static void EmitStep(ThumbEmitter emitter, AgbKeys key, LowRegister axis, bool increases, int limit) {
        var skip = emitter.NewLabel();

        emitter.MoveImmediate(destination: LowRegister.R2, value: ((byte)key));
        emitter.Alu(destination: LowRegister.R5, op: ThumbAlu.Test, source: LowRegister.R2);
        emitter.Branch(condition: ThumbCondition.Equal, label: skip);
        emitter.MoveImmediate(destination: LowRegister.R2, value: ((byte)limit));
        emitter.Alu(destination: axis, op: ThumbAlu.Compare, source: LowRegister.R2);

        if (increases) {
            emitter.Branch(condition: ThumbCondition.CarrySet, label: skip);
            emitter.AddImmediate(register: axis, value: OrbChaseProtocol.SquareSize);
        } else {
            emitter.Branch(condition: ThumbCondition.CarryClear, label: skip);
            emitter.SubtractImmediate(register: axis, value: OrbChaseProtocol.SquareSize);
        }

        emitter.MarkLabel(label: skip);
    }
    // placeTarget: two PRNG draws reduced onto the 30×20 grid; a placement that would land on the player's cell is
    // nudged one column right (wrapping to column 0), which can never re-collide because only the x changes.
    private static void EmitPlaceTarget(ThumbEmitter emitter, AgbForgeKernel kernel, Labels labels) {
        var store = emitter.NewLabel();

        emitter.MarkLabel(label: labels.PlaceTarget);
        emitter.Push(includeLinkRegister: true, registers: LowRegisterMask.R4);
        kernel.EmitPrngNextCall();
        EmitModulo(emitter: emitter, modulus: OrbChaseProtocol.GridColumns);
        emitter.ShiftImmediate(amount: 3, destination: LowRegister.R0, op: ThumbShift.LogicalLeft, source: LowRegister.R0);
        emitter.MoveRegister(destination: LowRegister.R4, source: LowRegister.R0);
        kernel.EmitPrngNextCall();
        EmitModulo(emitter: emitter, modulus: OrbChaseProtocol.GridRows);
        emitter.ShiftImmediate(amount: 3, destination: LowRegister.R0, op: ThumbShift.LogicalLeft, source: LowRegister.R0);

        emitter.LoadConstant(destination: LowRegister.R1, value: AgbForgeMemoryMap.GameRam);
        emitter.LoadHalf(baseRegister: LowRegister.R1, byteOffset: OrbChaseProtocol.PlayerXOffset, destination: LowRegister.R2);
        emitter.Alu(destination: LowRegister.R4, op: ThumbAlu.Compare, source: LowRegister.R2);
        emitter.Branch(condition: ThumbCondition.NotEqual, label: store);
        emitter.LoadHalf(baseRegister: LowRegister.R1, byteOffset: OrbChaseProtocol.PlayerYOffset, destination: LowRegister.R2);
        emitter.Alu(destination: LowRegister.R0, op: ThumbAlu.Compare, source: LowRegister.R2);
        emitter.Branch(condition: ThumbCondition.NotEqual, label: store);
        emitter.AddImmediate(register: LowRegister.R4, value: OrbChaseProtocol.SquareSize);
        emitter.MoveImmediate(destination: LowRegister.R2, value: ((byte)OrbChaseProtocol.MaxX));
        emitter.Alu(destination: LowRegister.R4, op: ThumbAlu.Compare, source: LowRegister.R2);
        emitter.Branch(condition: ThumbCondition.UnsignedLowerOrSame, label: store);
        emitter.MoveImmediate(destination: LowRegister.R4, value: 0);

        emitter.MarkLabel(label: store);
        emitter.StoreHalf(baseRegister: LowRegister.R1, byteOffset: OrbChaseProtocol.TargetXOffset, source: LowRegister.R4);
        emitter.StoreHalf(baseRegister: LowRegister.R1, byteOffset: OrbChaseProtocol.TargetYOffset, source: LowRegister.R0);
        emitter.Pop(includeProgramCounter: true, registers: LowRegisterMask.R4);
    }
    // r0 %= modulus by repeated subtraction (r0 starts as a PRNG byte, 0..255).
    private static void EmitModulo(ThumbEmitter emitter, byte modulus) {
        var reduce = emitter.NewLabel();
        var done = emitter.NewLabel();

        emitter.MarkLabel(label: reduce);
        emitter.CompareImmediate(register: LowRegister.R0, value: modulus);
        emitter.Branch(condition: ThumbCondition.CarryClear, label: done);
        emitter.SubtractImmediate(register: LowRegister.R0, value: modulus);
        emitter.Branch(label: reduce);
        emitter.MarkLabel(label: done);
    }
    // drawPlayer/drawTarget: an 8×8 fill at the entity's stored position.
    private static void EmitDrawSquare(ThumbEmitter emitter, AgbForgeKernel kernel, int label, int xOffset, int yOffset, ushort colour) {
        emitter.MarkLabel(label: label);
        emitter.Push(includeLinkRegister: true, registers: LowRegisterMask.R4);
        emitter.LoadConstant(destination: LowRegister.R3, value: AgbForgeMemoryMap.GameRam);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: xOffset, destination: LowRegister.R0);
        emitter.LoadHalf(baseRegister: LowRegister.R3, byteOffset: yOffset, destination: LowRegister.R1);
        emitter.MoveImmediate(destination: LowRegister.R2, value: OrbChaseProtocol.SquareSize);
        emitter.MoveImmediate(destination: LowRegister.R3, value: OrbChaseProtocol.SquareSize);
        emitter.LoadConstant(destination: LowRegister.R4, value: colour);
        kernel.EmitFillRectCall();
        emitter.Pop(includeProgramCounter: true, registers: LowRegisterMask.R4);
    }
    // A full-screen fill; also draws the title bar when the colour is the backdrop at boot time.
    private static void EmitFillScreenCall(ThumbEmitter emitter, AgbForgeKernel kernel, ushort colour) {
        emitter.MoveImmediate(destination: LowRegister.R0, value: 0);
        emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
        emitter.MoveImmediate(destination: LowRegister.R2, value: ((byte)AgbHw.ScreenWidth));
        emitter.MoveImmediate(destination: LowRegister.R3, value: ((byte)AgbHw.ScreenHeight));
        emitter.LoadConstant(destination: LowRegister.R4, value: colour);
        kernel.EmitFillRectCall();
    }
}
