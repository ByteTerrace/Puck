namespace Puck.ShaderVm;

/// <summary>Identifies one generic operation over four-lane shader values.</summary>
public enum ShaderOp : byte {
    /// <summary>Pushes an execution-context input selected by the instruction operand.</summary>
    LoadInput = 0,
    /// <summary>Pushes a caller-supplied parameter selected by the instruction operand.</summary>
    LoadParameter = 1,
    /// <summary>Pushes a program constant selected by the instruction operand.</summary>
    LoadConstant = 2,
    /// <summary>Pushes the program constant the top value's first lane indexes, clamped to the pool.</summary>
    LoadConstantDynamic = 3,
    /// <summary>Pushes the local register selected by the instruction operand.</summary>
    LoadLocal = 4,
    /// <summary>Pops the top value into the local register selected by the instruction operand.</summary>
    StoreLocal = 5,
    /// <summary>Rearranges the top value's lanes by four two-bit selectors packed into the operand.</summary>
    Swizzle = 6,
    /// <summary>Pushes a copy of the top value.</summary>
    Duplicate = 7,
    /// <summary>Exchanges the top two values.</summary>
    Swap = 8,
    /// <summary>Discards the top value.</summary>
    Drop = 9,
    /// <summary>Pushes a copy of the value the operand counts back from the top.</summary>
    Pick = 10,

    /// <summary>Takes the component-wise absolute value.</summary>
    Absolute = 16,
    /// <summary>Negates the top value.</summary>
    Negate = 17,
    /// <summary>Takes the component-wise floor.</summary>
    Floor = 18,
    /// <summary>Takes the component-wise ceiling.</summary>
    Ceiling = 19,
    /// <summary>Takes the component-wise fractional part.</summary>
    Fraction = 20,
    /// <summary>Clamps the top value to zero through one.</summary>
    Saturate = 21,
    /// <summary>Truncates toward zero.</summary>
    Truncate = 22,
    /// <summary>Rounds to the nearest integer, ties to even.</summary>
    Round = 23,
    /// <summary>Takes the component-wise sign as minus one, zero, or one.</summary>
    Sign = 24,
    /// <summary>Takes the component-wise reciprocal.</summary>
    Reciprocal = 25,
    /// <summary>Takes the component-wise square root.</summary>
    SquareRoot = 26,
    /// <summary>Takes the component-wise reciprocal square root.</summary>
    InverseSquareRoot = 27,
    /// <summary>Raises e to the top value, component-wise.</summary>
    Exponential = 28,
    /// <summary>Takes the component-wise natural logarithm.</summary>
    NaturalLogarithm = 29,
    /// <summary>Takes the component-wise sine.</summary>
    Sine = 30,
    /// <summary>Takes the component-wise cosine.</summary>
    Cosine = 31,
    /// <summary>Normalizes the first two lanes and clears the rest.</summary>
    Normalize2 = 32,
    /// <summary>Normalizes the first three lanes and clears the fourth.</summary>
    Normalize3 = 33,
    /// <summary>Replaces every lane with the first two lanes' length.</summary>
    Length2 = 34,
    /// <summary>Replaces every lane with the first three lanes' length.</summary>
    Length3 = 35,
    /// <summary>Reinterprets the first three lanes as unsigned bit patterns, hashes them with PCG3D, and returns the raw result bits.</summary>
    Hash3 = 36,
    /// <summary>Reinterprets each lane as an unsigned bit pattern and scales it into the unit interval.</summary>
    BitsToUnitFloat = 37,
    /// <summary>Truncates each lane to an unsigned integer and returns that integer's bit pattern as a float.</summary>
    IntegerBits = 38,

    /// <summary>Adds the top two values.</summary>
    Add = 64,
    /// <summary>Subtracts the top value from the value beneath it.</summary>
    Subtract = 65,
    /// <summary>Multiplies the top two values.</summary>
    Multiply = 66,
    /// <summary>Divides the value beneath the top by the top value.</summary>
    Divide = 67,
    /// <summary>Takes the component-wise minimum of the top two values.</summary>
    Minimum = 68,
    /// <summary>Takes the component-wise maximum of the top two values.</summary>
    Maximum = 69,
    /// <summary>Raises the value beneath the top to the component-wise power on top.</summary>
    Power = 70,
    /// <summary>Takes the component-wise floating-point remainder.</summary>
    Modulo = 71,
    /// <summary>Applies a component-wise step to the top two values.</summary>
    Step = 72,
    /// <summary>Replaces the top two values with their two-lane dot product replicated to every lane.</summary>
    Dot2 = 73,
    /// <summary>Replaces the top two values with their three-lane dot product replicated to every lane.</summary>
    Dot3 = 74,
    /// <summary>Replaces the top two values with their three-lane cross product.</summary>
    Cross3 = 75,
    /// <summary>Produces one where the value beneath the top is less than the top, and zero elsewhere.</summary>
    Less = 76,
    /// <summary>Produces one where the value beneath the top is greater than the top, and zero elsewhere.</summary>
    Greater = 77,
    /// <summary>Takes the angle to the point whose ordinate is beneath the top and whose abscissa is on top.</summary>
    ArcTangent2 = 78,

    /// <summary>Linearly interpolates the first two values by the third.</summary>
    Lerp = 128,
    /// <summary>Applies component-wise smoothstep to the top three values.</summary>
    SmoothStep = 129,
    /// <summary>Clamps the first value to the second and third.</summary>
    Clamp = 130,
    /// <summary>Selects the second value where the third is non-zero and the first elsewhere.</summary>
    Select = 131,

    /// <summary>Transfers control to the instruction the operand names, which must lie ahead.</summary>
    Jump = 192,
    /// <summary>Pops a value and transfers control to the operand's instruction when its first lane is zero.</summary>
    JumpIfZero = 193,

    /// <summary>Samples the deterministic lattice noise at the top value's first two lanes, seeded by its fourth.</summary>
    ValueNoise2 = 240,
    /// <summary>Samples the deterministic lattice noise at the top value's first three lanes, seeded by its fourth.</summary>
    ValueNoise3 = 241,
    /// <summary>Sums the operand's count of lattice-noise octaves at the top value, halving amplitude and doubling frequency each time.</summary>
    Fbm2 = 242,

    /// <summary>Ends evaluation and returns the single remaining value.</summary>
    Halt = 255,
}
