using Puck.Maths;

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The advanced machine's rotating and scaling background: a table of turns baked into the image, and the per-frame
/// arithmetic that turns an angle, a zoom and a centre into the four matrix registers and the reference point.
/// </summary>
/// <remarks>
/// <para>
/// Units: angle is a whole turn in 256 steps, zoom is in sixteenths (16 is life size), and the matrix registers are
/// signed 8.8 while the reference point is signed 20.8. The baked table holds one turn of sine in 8.8, so cosine is
/// the same table read a quarter turn along.
/// </para>
/// <para>
/// The matrix maps screen back to texture, so it carries the INVERSE zoom: a layer drawn at half size steps through
/// twice as much texture per pixel. That is why the entries scale by the sixteenths value rather than its reciprocal.
/// </para>
/// </remarks>
public sealed class AgbAffineBackground {
    private const uint ControlAddress = 0x0400000Cu;
    private const uint MatrixAddress = 0x04000020u;
    private const uint ReferenceXAddress = 0x04000028u;
    private const uint ReferenceYAddress = 0x0400002Cu;

    /// <summary>Steps in a whole turn; the angle value wraps within a byte.</summary>
    public const int TurnSteps = 256;
    /// <summary>The zoom value that leaves the layer life size.</summary>
    public const int UnitScale = 16;

    private readonly ThumbEmitter m_emitter;
    private readonly uint m_tableAddress;

    /// <summary>Creates the helper over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="tableAddress">The baked turn table's address in the cartridge image.</param>
    public AgbAffineBackground(ThumbEmitter emitter, uint tableAddress) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_emitter = emitter;
        m_tableAddress = tableAddress;
    }

    private void EmitReference(LowRegister first, LowRegister second, bool negateSecond, Action<LowRegister> centreA, Action<LowRegister> centreB, Action<LowRegister> self, uint address) {
        centreA(LowRegister.R0);
        m_emitter.Alu(
            destination: LowRegister.R0,
            op: ThumbAlu.Multiply,
            source: first
        );
        centreB(LowRegister.R1);
        m_emitter.Alu(
            destination: LowRegister.R1,
            op: ThumbAlu.Multiply,
            source: second
        );
        if (negateSecond) {
            m_emitter.SubtractRegister(
                destination: LowRegister.R0,
                operand: LowRegister.R1,
                source: LowRegister.R0
            );
        } else {
            m_emitter.AddRegister(
                destination: LowRegister.R0,
                operand: LowRegister.R1,
                source: LowRegister.R0
            );
        }

        self(LowRegister.R1);
        m_emitter.ShiftImmediate(
            amount: 8,
            destination: LowRegister.R1,
            op: ThumbShift.LogicalLeft,
            source: LowRegister.R1
        );
        m_emitter.SubtractRegister(
            destination: LowRegister.R0,
            operand: LowRegister.R0,
            source: LowRegister.R1
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R2,
            value: address
        );
        m_emitter.StoreWord(
            baseRegister: LowRegister.R2,
            byteOffset: 0,
            source: LowRegister.R0
        );
    }
    private void EmitScale(LowRegister register) {
        m_emitter.Alu(
            destination: register,
            op: ThumbAlu.Multiply,
            source: LowRegister.R5
        );
        m_emitter.ShiftImmediate(
            amount: 4,
            destination: register,
            op: ThumbShift.ArithmeticRight,
            source: register
        );
    }
    private void StoreHalfFrom(LowRegister register, uint address) {
        m_emitter.LoadConstant(
            destination: LowRegister.R2,
            value: address
        );
        m_emitter.StoreHalf(
            baseRegister: LowRegister.R2,
            byteOffset: 0,
            source: register
        );
    }

    /// <summary>Bakes one turn of sine as signed 8.8 halfwords, computed in the house fixed point rather than floats.</summary>
    /// <returns>The table bytes.</returns>
    public static byte[] BuildTurnTable() {
        var values = new int[TurnSteps];

        for (var step = 0; (step < TurnSteps); ++step) {
            // A turn fraction scaled by 2^64 divides exactly by 256, so no angle is rounded on the way in.
            var (sin, _) = FixedQ4816.SinCosTurns(fractionalTurns: (((ulong)step) << 56));
            values[step] = ((int)((sin.Value * 256L) >> FixedQ4816.FractionBitCount));
        }

        var bytes = new byte[(TurnSteps * 2)];

        for (var step = 0; (step < TurnSteps); ++step) {
            bytes[(step * 2)] = ((byte)(values[step] & 0xFF));
            bytes[((step * 2) + 1)] = ((byte)((values[step] >> 8) & 0xFF));
        }

        return bytes;
    }
    /// <summary>Returns the control word for a map of the given cell count.</summary>
    /// <param name="cellCount">The map's cell count: 256, 1024, 4096 or 16384.</param>
    /// <param name="screenBlock">The screen block holding the map.</param>
    /// <returns>The control halfword.</returns>
    public static uint Control(int cellCount, int screenBlock) => cellCount switch {
        256 => ((uint)((screenBlock << 8) | 0x0000)),
        1024 => ((uint)((screenBlock << 8) | 0x4000)),
        4096 => ((uint)((screenBlock << 8) | 0x8000)),
        _ => ((uint)((screenBlock << 8) | 0xC000)),
    };
    /// <summary>Emits the layer's control register.</summary>
    /// <param name="control">The control halfword from <see cref="Control"/>.</param>
    public void EmitControl(uint control) {
        m_emitter.LoadConstant(
            destination: LowRegister.R0,
            value: control
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R2,
            value: ControlAddress
        );
        m_emitter.StoreHalf(
            baseRegister: LowRegister.R2,
            byteOffset: 0,
            source: LowRegister.R0
        );
    }
    /// <summary>Emits the per-frame matrix and reference point from the angle, zoom and centre already in memory.</summary>
    /// <param name="angle">Loads the angle into the given register.</param>
    /// <param name="scale">Loads the zoom into the given register.</param>
    /// <param name="centreX">Loads the centre column into the given register.</param>
    /// <param name="centreY">Loads the centre row into the given register.</param>
    public void EmitPlace(Action<LowRegister> angle, Action<LowRegister> scale, Action<LowRegister> centreX, Action<LowRegister> centreY) {
        ArgumentNullException.ThrowIfNull(argument: angle);
        ArgumentNullException.ThrowIfNull(argument: scale);
        ArgumentNullException.ThrowIfNull(argument: centreX);
        ArgumentNullException.ThrowIfNull(argument: centreY);

        // r6 = sine, r7 = cosine, both signed 8.8 and already carrying the zoom.
        angle(LowRegister.R0);
        scale(LowRegister.R5);
        AgbTurnLookup.Emit(
            destination: LowRegister.R6,
            emitter: m_emitter,
            quarterTurns: 0,
            tableAddress: m_tableAddress
        );
        angle(LowRegister.R0);
        AgbTurnLookup.Emit(
            destination: LowRegister.R7,
            emitter: m_emitter,
            quarterTurns: 64,
            tableAddress: m_tableAddress
        );
        EmitScale(register: LowRegister.R6);
        EmitScale(register: LowRegister.R7);

        // The matrix: cosine, negated sine, sine, cosine.
        StoreHalfFrom(
            address: MatrixAddress,
            register: LowRegister.R7
        );
        m_emitter.MoveImmediate(
            destination: LowRegister.R0,
            value: 0
        );
        m_emitter.SubtractRegister(
            destination: LowRegister.R0,
            operand: LowRegister.R6,
            source: LowRegister.R0
        );
        StoreHalfFrom(
            address: (MatrixAddress + 2),
            register: LowRegister.R0
        );
        StoreHalfFrom(
            address: (MatrixAddress + 4),
            register: LowRegister.R6
        );
        StoreHalfFrom(
            address: (MatrixAddress + 6),
            register: LowRegister.R7
        );

        // Reference = (centre << 8) - (matrix row . centre), so the centre pixel maps to itself.
        EmitReference(
            address: ReferenceXAddress,
            centreA: centreX,
            centreB: centreY,
            first: LowRegister.R7,
            negateSecond: true,
            second: LowRegister.R6,
            self: centreX
        );
        EmitReference(
            address: ReferenceYAddress,
            centreA: centreX,
            centreB: centreY,
            first: LowRegister.R6,
            negateSecond: false,
            second: LowRegister.R7,
            self: centreY
        );
    }
}
