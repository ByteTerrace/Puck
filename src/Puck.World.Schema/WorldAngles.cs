using Puck.Maths;

namespace Puck.World;

/// <summary>The angle conversion factors an authored document's degree fields compile through.</summary>
internal static class WorldAngles {
    /// <summary>Radians per degree, quantized once so every fixed-point degree-to-radian multiply shares one factor.</summary>
    /// <remarks>Multiplying an already-quantized degree value by this factor is NOT the same rounding as quantizing
    /// the double product (<c>FixedQ4816.FromDouble(degrees * (Math.PI / 180.0))</c>): the two shapes differ in the
    /// last bits, so a call site written against one of them keeps it.</remarks>
    internal static readonly FixedQ4816 DegreesToRadians = FixedQ4816.FromDouble(value: (Math.PI / 180.0));
}
