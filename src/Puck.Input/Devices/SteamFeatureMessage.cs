namespace Puck.Input.Devices;

/// <summary>Frames the vendor feature message both Steam controller families send: <c>[report id][command][payload
/// length][payload]</c> zero-padded to the device's feature report length. The classic pad writes it under report id 0
/// and the Triton under report id 1.</summary>
internal static class SteamFeatureMessage {
    /// <summary>Writes one message into <paramref name="buffer"/>, clearing every byte past the payload.</summary>
    /// <param name="buffer">The feature report buffer, at least three bytes plus the payload.</param>
    /// <param name="reportId">The HID report id for byte 0.</param>
    /// <param name="command">The vendor command for byte 1.</param>
    /// <param name="payload">The command's payload, at most 255 bytes.</param>
    public static void Write(Span<byte> buffer, byte reportId, byte command, ReadOnlySpan<byte> payload) {
        buffer.Clear();
        buffer[0] = reportId;
        buffer[1] = command;
        buffer[2] = ((byte)payload.Length);
        payload.CopyTo(destination: buffer[3..]);
    }
}
