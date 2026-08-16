using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Input.Devices;

/// <summary>Owns the signed little-endian stick decoding shared by Steam-family input reports.</summary>
internal static class SteamControllerInput {
    private const float StickDeadzone = 0.12f;
    private const float StickRange = 32768f;

    /// <summary>Reads one Steam-family stick and applies the family radial deadzone.</summary>
    public static Vector2 ReadStick(ReadOnlySpan<byte> report, int offset) {
        var stick = new Vector2(
            x: (BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]) / StickRange),
            y: (BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..]) / StickRange)
        );

        return GamepadNormalization.ApplyRadialDeadzone(
            deadzone: StickDeadzone,
            stick: stick
        );
    }
}
