namespace Puck.AdvancedGamingBrick;

/// <summary>Specifies the barrel-shifter operation applied to an ARM shifter operand, encoded in two bits of
/// the instruction.</summary>
public enum ShiftType : uint {
    /// <summary>Logical shift left.</summary>
    LogicalLeft = 0,
    /// <summary>Logical shift right (zero-fill).</summary>
    LogicalRight = 1,
    /// <summary>Arithmetic shift right (sign-fill).</summary>
    ArithmeticRight = 2,
    /// <summary>Rotate right; with an immediate amount of zero this becomes rotate-right-with-extend (RRX).</summary>
    RotateRight = 3,
}
