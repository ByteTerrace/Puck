namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// The multiply, shift and divide helpers the document compiler calls for operations SM83 has no instruction for.
/// Every routine takes the left operand in A and the right in B and returns its result in A; the divide also leaves the
/// remainder in C. All results are unsigned and wrap modulo 256, matching the Thumb backend's masked arithmetic.
/// </summary>
/// <remarks>
/// Division or modulo by zero yields zero, and a shift of eight or more yields zero; both are total so a runtime
/// operand can never trap. Routines clobber B, C, D and E; HL is preserved.
/// </remarks>
internal sealed class Sm83CartridgeArithmetic {
    private readonly Sm83Emitter m_emitter;
    private readonly int m_divideLabel;
    private readonly int m_multiplyLabel;
    private readonly int m_shiftLeftLabel;
    private readonly int m_shiftRightLabel;

    public Sm83CartridgeArithmetic(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_emitter = emitter;
        m_divideLabel = emitter.NewLabel();
        m_multiplyLabel = emitter.NewLabel();
        m_shiftLeftLabel = emitter.NewLabel();
        m_shiftRightLabel = emitter.NewLabel();
    }

    /// <summary>Emits a call that replaces A with the product of A and B.</summary>
    public void EmitMultiply() => m_emitter.Call(label: m_multiplyLabel);
    /// <summary>Emits a call that replaces A with A divided by B, leaving the remainder in C.</summary>
    public void EmitDivide() => m_emitter.Call(label: m_divideLabel);
    /// <summary>Emits a call that replaces A with A shifted left by B.</summary>
    public void EmitShiftLeft() => m_emitter.Call(label: m_shiftLeftLabel);
    /// <summary>Emits a call that replaces A with A shifted right by B.</summary>
    public void EmitShiftRight() => m_emitter.Call(label: m_shiftRightLabel);

    /// <summary>Emits every routine's body. Call once, outside the frame loop.</summary>
    public void EmitLibrary() {
        EmitMultiplyBody();
        EmitShiftBody(label: m_shiftLeftLabel, op: ShiftOp.ShiftLeftArithmetic);
        EmitShiftBody(label: m_shiftRightLabel, op: ShiftOp.ShiftRightLogical);
        EmitDivideBody();
    }

    // Shift-and-add: C holds the running multiplicand, B is consumed a bit at a time, and the loop exits when B is
    // empty, so a zero multiplier returns zero without a special case.
    private void EmitMultiplyBody() {
        var loop = m_emitter.NewLabel();
        var skip = m_emitter.NewLabel();
        m_emitter.MarkLabel(label: m_multiplyLabel);
        m_emitter.Load(destination: Reg8.C, source: Reg8.A);
        m_emitter.XorA();
        m_emitter.MarkLabel(label: loop);
        m_emitter.TestBit(bit: 0, register: Reg8.B);
        m_emitter.JumpRelative(condition: Condition.Zero, label: skip);
        m_emitter.Arithmetic(op: AluOp.Add, source: Reg8.C);
        m_emitter.MarkLabel(label: skip);
        m_emitter.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.C);
        m_emitter.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.B);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: loop);
        m_emitter.Return();
    }

    // A count of eight or more can only produce zero, so it short-circuits instead of running a pointless loop.
    private void EmitShiftBody(int label, ShiftOp op) {
        var done = m_emitter.NewLabel();
        var loop = m_emitter.NewLabel();
        var zero = m_emitter.NewLabel();
        m_emitter.MarkLabel(label: label);
        m_emitter.Load(destination: Reg8.C, source: Reg8.A);
        m_emitter.Load(destination: Reg8.A, source: Reg8.B);
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: 8);
        m_emitter.JumpRelative(condition: Condition.NoCarry, label: zero);
        m_emitter.Load(destination: Reg8.A, source: Reg8.C);
        m_emitter.Increment(register: Reg8.B);
        m_emitter.MarkLabel(label: loop);
        m_emitter.Decrement(register: Reg8.B);
        m_emitter.JumpRelative(condition: Condition.Zero, label: done);
        m_emitter.Shift(op: op, register: Reg8.A);
        m_emitter.JumpRelative(label: loop);
        m_emitter.MarkLabel(label: zero);
        m_emitter.XorA();
        m_emitter.MarkLabel(label: done);
        m_emitter.Return();
    }

    // Restoring division: each of the eight steps shifts the dividend left through the remainder in C, subtracts the
    // divisor when it fits, and sets the quotient bit the same shift just vacated.
    private void EmitDivideBody() {
        var loop = m_emitter.NewLabel();
        var next = m_emitter.NewLabel();
        var skip = m_emitter.NewLabel();
        var zero = m_emitter.NewLabel();
        m_emitter.MarkLabel(label: m_divideLabel);
        m_emitter.Load(destination: Reg8.D, source: Reg8.A);
        m_emitter.Load(destination: Reg8.A, source: Reg8.B);
        m_emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        m_emitter.JumpRelative(condition: Condition.Zero, label: zero);
        m_emitter.Load(destination: Reg8.A, source: Reg8.D);
        m_emitter.LoadImmediate(destination: Reg8.C, value: 0);
        m_emitter.LoadImmediate(destination: Reg8.E, value: 8);
        m_emitter.MarkLabel(label: loop);
        m_emitter.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        m_emitter.Shift(op: ShiftOp.RotateLeft, register: Reg8.C);
        m_emitter.Load(destination: Reg8.D, source: Reg8.A);
        m_emitter.Load(destination: Reg8.A, source: Reg8.C);
        m_emitter.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        m_emitter.JumpRelative(condition: Condition.Carry, label: skip);
        m_emitter.Load(destination: Reg8.C, source: Reg8.A);
        m_emitter.Load(destination: Reg8.A, source: Reg8.D);
        m_emitter.ArithmeticImmediate(op: AluOp.Or, value: 1);
        m_emitter.JumpRelative(label: next);
        m_emitter.MarkLabel(label: skip);
        m_emitter.Load(destination: Reg8.A, source: Reg8.D);
        m_emitter.MarkLabel(label: next);
        m_emitter.Decrement(register: Reg8.E);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: loop);
        m_emitter.Return();
        m_emitter.MarkLabel(label: zero);
        m_emitter.XorA();
        m_emitter.Load(destination: Reg8.C, source: Reg8.A);
        m_emitter.Return();
    }
}
