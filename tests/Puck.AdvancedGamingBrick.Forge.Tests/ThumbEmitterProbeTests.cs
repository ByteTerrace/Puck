namespace Puck.AdvancedGamingBrick.Forge.Tests;

// Each probe assembles a short Thumb sequence with the emitter, wraps it in a direct-boot cartridge, executes it
// on a real machine, and asserts register/memory effects — the emulator core is the encoding oracle: when a probe
// disagrees with the core, the emitter is wrong.
public sealed class ThumbEmitterProbeTests {
    // Game-free EWRAM scratch (above the framework block and the example cart's fields).
    private const uint ScratchBase = 0x02000200u;

    [Fact]
    public void MoveAddSubtractCompareImmediates() {
        using var driver = RunProbe(emit: static emitter => {
            emitter.MoveImmediate(destination: LowRegister.R0, value: 10);
            emitter.MoveImmediate(destination: LowRegister.R1, value: 3);
            emitter.SubtractRegister(destination: LowRegister.R2, operand: LowRegister.R1, source: LowRegister.R0);
            emitter.AddRegister(destination: LowRegister.R3, operand: LowRegister.R1, source: LowRegister.R0);
            emitter.AddImmediate(register: LowRegister.R0, value: 0xFF);
            emitter.SubtractImmediate3(destination: LowRegister.R4, source: LowRegister.R0, value: 7);
            emitter.AddImmediate3(destination: LowRegister.R6, source: LowRegister.R1, value: 4);

            var skip = emitter.NewLabel();

            emitter.MoveImmediate(destination: LowRegister.R5, value: 0);
            emitter.CompareImmediate(register: LowRegister.R1, value: 3);
            emitter.Branch(condition: ThumbCondition.NotEqual, label: skip);
            emitter.MoveImmediate(destination: LowRegister.R5, value: 1);
            emitter.MarkLabel(label: skip);
        });

        Assert.Equal(expected: 7u, actual: driver.ReadRegister(index: 2));
        Assert.Equal(expected: 13u, actual: driver.ReadRegister(index: 3));
        Assert.Equal(expected: 265u, actual: driver.ReadRegister(index: 0));
        Assert.Equal(expected: 258u, actual: driver.ReadRegister(index: 4));
        Assert.Equal(expected: 1u, actual: driver.ReadRegister(index: 5));
        Assert.Equal(expected: 7u, actual: driver.ReadRegister(index: 6));
    }
    [Fact]
    public void AluRegisterOperations() {
        using var driver = RunProbe(emit: static emitter => {
            emitter.MoveImmediate(destination: LowRegister.R0, value: 0xF0);
            emitter.MoveImmediate(destination: LowRegister.R1, value: 0x3C);
            emitter.MoveRegister(destination: LowRegister.R2, source: LowRegister.R0);
            emitter.Alu(destination: LowRegister.R2, op: ThumbAlu.And, source: LowRegister.R1);
            emitter.MoveRegister(destination: LowRegister.R3, source: LowRegister.R0);
            emitter.Alu(destination: LowRegister.R3, op: ThumbAlu.Or, source: LowRegister.R1);
            emitter.MoveRegister(destination: LowRegister.R4, source: LowRegister.R0);
            emitter.Alu(destination: LowRegister.R4, op: ThumbAlu.ExclusiveOr, source: LowRegister.R1);
            emitter.MoveImmediate(destination: LowRegister.R5, value: 7);
            emitter.Alu(destination: LowRegister.R5, op: ThumbAlu.Multiply, source: LowRegister.R1);
            emitter.Alu(destination: LowRegister.R6, op: ThumbAlu.MoveNegated, source: LowRegister.R0);
            emitter.MoveImmediate(destination: LowRegister.R7, value: 5);
            emitter.Alu(destination: LowRegister.R7, op: ThumbAlu.Negate, source: LowRegister.R7);
        });

        Assert.Equal(expected: 0x30u, actual: driver.ReadRegister(index: 2));
        Assert.Equal(expected: 0xFCu, actual: driver.ReadRegister(index: 3));
        Assert.Equal(expected: 0xCCu, actual: driver.ReadRegister(index: 4));
        Assert.Equal(expected: 420u, actual: driver.ReadRegister(index: 5));
        Assert.Equal(expected: 0xFFFFFF0Fu, actual: driver.ReadRegister(index: 6));
        Assert.Equal(expected: unchecked((uint)(-5)), actual: driver.ReadRegister(index: 7));
    }
    [Fact]
    public void ShiftImmediateAndRegisterShifts() {
        using var driver = RunProbe(emit: static emitter => {
            emitter.MoveImmediate(destination: LowRegister.R0, value: 1);
            emitter.ShiftImmediate(amount: 31, destination: LowRegister.R1, op: ThumbShift.LogicalLeft, source: LowRegister.R0);
            emitter.ShiftImmediate(amount: 4, destination: LowRegister.R2, op: ThumbShift.LogicalRight, source: LowRegister.R1);
            emitter.ShiftImmediate(amount: 4, destination: LowRegister.R3, op: ThumbShift.ArithmeticRight, source: LowRegister.R1);
            emitter.MoveImmediate(destination: LowRegister.R4, value: 4);
            emitter.MoveImmediate(destination: LowRegister.R5, value: 0x80);
            emitter.Alu(destination: LowRegister.R5, op: ThumbAlu.LogicalLeft, source: LowRegister.R4);
            emitter.MoveImmediate(destination: LowRegister.R6, value: 0x10);
            emitter.Alu(destination: LowRegister.R6, op: ThumbAlu.RotateRight, source: LowRegister.R4);
        });

        Assert.Equal(expected: 0x80000000u, actual: driver.ReadRegister(index: 1));
        Assert.Equal(expected: 0x08000000u, actual: driver.ReadRegister(index: 2));
        Assert.Equal(expected: 0xF8000000u, actual: driver.ReadRegister(index: 3));
        Assert.Equal(expected: 0x800u, actual: driver.ReadRegister(index: 5));
        Assert.Equal(expected: 0x1u, actual: driver.ReadRegister(index: 6));
    }
    [Fact]
    public void HiRegisterOperations() {
        using var driver = RunProbe(emit: static emitter => {
            emitter.MoveImmediate(destination: LowRegister.R0, value: 5);
            emitter.MoveHigh(destination: CoreRegister.R8, source: CoreRegister.R0);
            emitter.AddHigh(destination: CoreRegister.R8, source: CoreRegister.R0);
            emitter.MoveHigh(destination: CoreRegister.R1, source: CoreRegister.R8);
            emitter.MoveHigh(destination: CoreRegister.R2, source: CoreRegister.Sp);
        });

        Assert.Equal(expected: 10u, actual: driver.ReadRegister(index: 8));
        Assert.Equal(expected: 10u, actual: driver.ReadRegister(index: 1));
        Assert.Equal(expected: 0x03007F00u, actual: driver.ReadRegister(index: 2));
    }
    [Fact]
    public void LiteralPoolLoads() {
        using var driver = RunProbe(emit: static emitter => {
            emitter.LoadConstant(destination: LowRegister.R0, value: 0xDEADBEEFu);
            emitter.LoadConstant(destination: LowRegister.R1, value: 0x12345678u);
            emitter.LoadConstant(destination: LowRegister.R2, value: 0xDEADBEEFu);
        });

        Assert.Equal(expected: 0xDEADBEEFu, actual: driver.ReadRegister(index: 0));
        Assert.Equal(expected: 0x12345678u, actual: driver.ReadRegister(index: 1));
        Assert.Equal(expected: 0xDEADBEEFu, actual: driver.ReadRegister(index: 2));
    }
    [Fact]
    public void LoadStoreImmediateOffsets() {
        using var driver = RunProbe(emit: static emitter => {
            emitter.LoadConstant(destination: LowRegister.R0, value: ScratchBase);
            emitter.LoadConstant(destination: LowRegister.R1, value: 0x11223344u);
            emitter.StoreWord(baseRegister: LowRegister.R0, byteOffset: 0, source: LowRegister.R1);
            emitter.StoreByte(baseRegister: LowRegister.R0, byteOffset: 8, source: LowRegister.R1);
            emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: 12, source: LowRegister.R1);
            emitter.LoadWord(baseRegister: LowRegister.R0, byteOffset: 0, destination: LowRegister.R2);
            emitter.LoadByte(baseRegister: LowRegister.R0, byteOffset: 8, destination: LowRegister.R3);
            emitter.LoadHalf(baseRegister: LowRegister.R0, byteOffset: 12, destination: LowRegister.R4);
        });

        Assert.Equal(expected: 0x11223344u, actual: driver.ReadRegister(index: 2));
        Assert.Equal(expected: 0x44u, actual: driver.ReadRegister(index: 3));
        Assert.Equal(expected: 0x3344u, actual: driver.ReadRegister(index: 4));
        Assert.Equal(expected: 0x11223344u, actual: driver.ReadWord(address: ScratchBase));
        Assert.Equal(expected: ((byte)0x44), actual: driver.ReadByte(address: (ScratchBase + 8u)));
        Assert.Equal(expected: ((ushort)0x3344), actual: driver.ReadHalf(address: (ScratchBase + 12u)));
    }
    [Fact]
    public void LoadStoreRegisterOffsetsAndSignExtension() {
        using var driver = RunProbe(emit: static emitter => {
            emitter.LoadConstant(destination: LowRegister.R0, value: ScratchBase);
            emitter.MoveImmediate(destination: LowRegister.R1, value: 0x20);
            emitter.LoadConstant(destination: LowRegister.R2, value: 0x000080FEu);
            emitter.StoreHalfRegister(baseRegister: LowRegister.R0, offsetRegister: LowRegister.R1, source: LowRegister.R2);
            emitter.LoadByteRegister(baseRegister: LowRegister.R0, destination: LowRegister.R3, offsetRegister: LowRegister.R1);
            emitter.LoadSignedByteRegister(baseRegister: LowRegister.R0, destination: LowRegister.R4, offsetRegister: LowRegister.R1);
            emitter.LoadHalfRegister(baseRegister: LowRegister.R0, destination: LowRegister.R5, offsetRegister: LowRegister.R1);
            emitter.LoadSignedHalfRegister(baseRegister: LowRegister.R0, destination: LowRegister.R6, offsetRegister: LowRegister.R1);
            emitter.StoreWordRegister(baseRegister: LowRegister.R0, offsetRegister: LowRegister.R1, source: LowRegister.R2);
            emitter.LoadWordRegister(baseRegister: LowRegister.R0, destination: LowRegister.R7, offsetRegister: LowRegister.R1);

            // Byte store/load through the register-offset byte forms (offset 0x21 is odd on purpose).
            emitter.AddImmediate(register: LowRegister.R1, value: 1);
            emitter.StoreByteRegister(baseRegister: LowRegister.R0, offsetRegister: LowRegister.R1, source: LowRegister.R2);
        });

        Assert.Equal(expected: 0xFEu, actual: driver.ReadRegister(index: 3));
        Assert.Equal(expected: 0xFFFFFFFEu, actual: driver.ReadRegister(index: 4));
        Assert.Equal(expected: 0x80FEu, actual: driver.ReadRegister(index: 5));
        Assert.Equal(expected: 0xFFFF80FEu, actual: driver.ReadRegister(index: 6));
        Assert.Equal(expected: 0x000080FEu, actual: driver.ReadRegister(index: 7));
        Assert.Equal(expected: ((byte)0xFE), actual: driver.ReadByte(address: (ScratchBase + 0x21u)));
    }
    [Fact]
    public void StackOperations() {
        using var driver = RunProbe(emit: static emitter => {
            emitter.MoveImmediate(destination: LowRegister.R0, value: 0x37);
            emitter.Push(includeLinkRegister: false, registers: LowRegisterMask.R0);
            emitter.Pop(includeProgramCounter: false, registers: LowRegisterMask.R1);
            emitter.AddToStackPointer(byteOffset: -8);
            emitter.MoveImmediate(destination: LowRegister.R2, value: 0x99);
            emitter.StoreSpRelative(byteOffset: 4, source: LowRegister.R2);
            emitter.LoadSpRelative(byteOffset: 4, destination: LowRegister.R3);
            emitter.AddToStackPointer(byteOffset: 8);
            emitter.MoveHigh(destination: CoreRegister.R4, source: CoreRegister.Sp);
        });

        Assert.Equal(expected: 0x37u, actual: driver.ReadRegister(index: 1));
        Assert.Equal(expected: 0x99u, actual: driver.ReadRegister(index: 3));
        Assert.Equal(expected: 0x03007F00u, actual: driver.ReadRegister(index: 4));
    }
    [Fact]
    public void ConditionalAndBackwardBranches() {
        using var driver = RunProbe(emit: static emitter => {
            var loop = emitter.NewLabel();
            var signedSkip = emitter.NewLabel();

            emitter.MoveImmediate(destination: LowRegister.R1, value: 5);
            emitter.MoveImmediate(destination: LowRegister.R2, value: 0);
            emitter.MarkLabel(label: loop);
            emitter.AddImmediate(register: LowRegister.R2, value: 1);
            emitter.SubtractImmediate(register: LowRegister.R1, value: 1);
            emitter.Branch(condition: ThumbCondition.NotEqual, label: loop);

            emitter.MoveImmediate(destination: LowRegister.R3, value: 1);
            emitter.Alu(destination: LowRegister.R3, op: ThumbAlu.Negate, source: LowRegister.R3);
            emitter.MoveImmediate(destination: LowRegister.R4, value: 0);
            emitter.CompareImmediate(register: LowRegister.R3, value: 0);
            emitter.Branch(condition: ThumbCondition.SignedGreaterOrEqual, label: signedSkip);
            emitter.MoveImmediate(destination: LowRegister.R4, value: 1);
            emitter.MarkLabel(label: signedSkip);
        });

        Assert.Equal(expected: 5u, actual: driver.ReadRegister(index: 2));
        Assert.Equal(expected: 1u, actual: driver.ReadRegister(index: 4));
    }
    [Fact]
    public void CallReturnAndNestedCalls() {
        using var driver = RunProbe(emit: static emitter => {
            var halt = emitter.NewLabel();
            var outer = emitter.NewLabel();
            var inner = emitter.NewLabel();

            emitter.MoveImmediate(destination: LowRegister.R0, value: 0);
            emitter.Call(label: outer);
            emitter.AddImmediate(register: LowRegister.R0, value: 1);
            emitter.MarkLabel(label: halt);
            emitter.Branch(label: halt);

            emitter.MarkLabel(label: outer);
            emitter.Push(includeLinkRegister: true, registers: LowRegisterMask.None);
            emitter.AddImmediate(register: LowRegister.R0, value: 100);
            emitter.Call(label: inner);
            emitter.Pop(includeProgramCounter: true, registers: LowRegisterMask.None);

            emitter.MarkLabel(label: inner);
            emitter.AddImmediate(register: LowRegister.R0, value: 10);
            emitter.BranchExchange(source: CoreRegister.Lr);
        });

        Assert.Equal(expected: 111u, actual: driver.ReadRegister(index: 0));
    }

    // Assembles the probe (a trailing self-loop is appended as the landing pad), builds a cartridge around it,
    // direct-boots a real machine, and steps far enough that over-stepping just spins on the pad.
    private static AgbVerifyMachineDriver RunProbe(Action<ThumbEmitter> emit, int steps = 256) {
        var emitter = new ThumbEmitter();

        emit(emitter);

        var halt = emitter.NewLabel();

        emitter.MarkLabel(label: halt);
        emitter.Branch(label: halt);

        var rom = AgbForgeCartridge.Build(
            data: [],
            gameCode: "PRBE",
            routine: emitter.ToArray(baseAddress: AgbForgeCartridge.CodeAddress),
            title: "PROBE"
        );
        var driver = new AgbVerifyMachineDriver(label: "probe", rom: rom);

        driver.StepInstructions(count: steps);

        return driver;
    }
}
