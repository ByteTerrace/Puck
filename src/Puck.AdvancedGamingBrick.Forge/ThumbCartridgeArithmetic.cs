namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The multiply, shift and divide helpers the document compiler emits for operations with no single Thumb-1 encoding,
/// or none that already agrees with the SM83 backend. Every helper takes the left operand in r0 and the right in r1 and
/// leaves its result in r0; the divide also leaves the remainder in r5.
/// </summary>
/// <remarks>
/// Results are masked to eight bits so they wrap exactly as the SM83 backend's do. Division or modulo by zero yields
/// zero, and a shift of eight or more yields zero; both are total so a runtime operand can never trap. The multiply and
/// shift helpers clobber r2; the divide clobbers r5.
/// </remarks>
internal sealed class ThumbCartridgeArithmetic {
    private readonly ThumbEmitter m_emitter;
    private readonly int m_divideLabel;

    public ThumbCartridgeArithmetic(ThumbEmitter emitter) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_emitter = emitter;
        m_divideLabel = emitter.NewLabel();
    }

    /// <summary>Emits the product of r0 and r1 into r0.</summary>
    public void EmitMultiply() {
        m_emitter.Alu(op: ThumbAlu.Multiply, destination: LowRegister.R0, source: LowRegister.R1);
        EmitMaskToByte();
    }

    /// <summary>Emits r0 shifted left by r1 into r0.</summary>
    public void EmitShiftLeft() {
        m_emitter.Alu(op: ThumbAlu.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R1);
        EmitMaskToByte();
    }

    /// <summary>Emits r0 shifted right by r1 into r0.</summary>
    public void EmitShiftRight() {
        m_emitter.Alu(op: ThumbAlu.LogicalRight, destination: LowRegister.R0, source: LowRegister.R1);
        EmitMaskToByte();
    }

    /// <summary>Emits a call that replaces r0 with r0 divided by r1, leaving the remainder in r5.</summary>
    public void EmitDivide() => m_emitter.Call(label: m_divideLabel);

    /// <summary>Emits the divide helper's body. Call once, outside the frame loop.</summary>
    public void EmitLibrary() {
        var done = m_emitter.NewLabel();
        var loop = m_emitter.NewLabel();
        var zero = m_emitter.NewLabel();
        m_emitter.MarkLabel(label: m_divideLabel);
        m_emitter.Push(registers: LowRegisterMask.None, includeLinkRegister: true);
        m_emitter.MoveRegister(destination: LowRegister.R5, source: LowRegister.R0);
        m_emitter.MoveImmediate(destination: LowRegister.R0, value: 0);
        m_emitter.CompareImmediate(register: LowRegister.R1, value: 0);
        m_emitter.Branch(condition: ThumbCondition.Equal, label: zero);
        m_emitter.MarkLabel(label: loop);
        m_emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R5, source: LowRegister.R1);
        m_emitter.Branch(condition: ThumbCondition.CarryClear, label: done);
        m_emitter.SubtractRegister(destination: LowRegister.R5, source: LowRegister.R5, operand: LowRegister.R1);
        m_emitter.AddImmediate(register: LowRegister.R0, value: 1);
        m_emitter.Branch(label: loop);
        m_emitter.MarkLabel(label: zero);
        m_emitter.MoveImmediate(destination: LowRegister.R5, value: 0);
        m_emitter.MarkLabel(label: done);
        m_emitter.Pop(registers: LowRegisterMask.None, includeProgramCounter: true);
    }

    private void EmitMaskToByte() {
        m_emitter.MoveImmediate(destination: LowRegister.R2, value: 255);
        m_emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R0, source: LowRegister.R2);
    }
}
