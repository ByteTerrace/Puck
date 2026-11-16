using Puck.Input.Hid;

namespace Puck.Input.Devices;

/// <summary>Sends the vendor feature message both Steam controller families use: <c>[report id][command][payload
/// length][payload]</c> zero-padded to the device's feature report length. The classic pad writes it under report id 0
/// and the Triton under report id 1. One channel owns one reusable buffer, so it belongs to the one thread that
/// drives its controller.</summary>
internal sealed class SteamFeatureChannel {
    private readonly byte[] m_buffer;
    private readonly IHidDevice m_device;
    private readonly byte m_reportId;

    /// <summary>Initializes a new instance of the <see cref="SteamFeatureChannel"/> class.</summary>
    /// <param name="device">The opened HID device handle.</param>
    /// <param name="minimumBytes">The documented feature report length; the buffer is floored to it because messages
    /// are composed at protocol offsets.</param>
    /// <param name="reportId">The HID report id for byte 0.</param>
    public SteamFeatureChannel(IHidDevice device, int minimumBytes, byte reportId) {
        m_device = device;
        m_reportId = reportId;
        m_buffer = new byte[Math.Max(
            val1: device.FeatureReportByteLength,
            val2: minimumBytes
        )];
    }

    /// <summary>Gets the channel's buffer, which holds the last message sent and receives a feature read.</summary>
    public byte[] Buffer => m_buffer;

    /// <summary>Writes one message synchronously.</summary>
    /// <param name="command">The vendor command for byte 1.</param>
    /// <param name="payload">The command's payload, at most 255 bytes.</param>
    /// <returns>Whether the device accepted the report.</returns>
    public bool Send(byte command, ReadOnlySpan<byte> payload) {
        Compose(
            command: command,
            payload: payload
        );

        return m_device.TrySetFeatureReport(buffer: m_buffer);
    }
    /// <summary>Writes one message, retrying a transient refusal.</summary>
    /// <param name="command">The vendor command for byte 1.</param>
    /// <param name="payload">The command's payload, at most 255 bytes.</param>
    /// <param name="cancellationToken">The token that ends the retry.</param>
    /// <returns>Whether the device accepted the report.</returns>
    public async ValueTask<bool> SendAsync(byte command, byte[] payload, CancellationToken cancellationToken) {
        Compose(
            command: command,
            payload: payload
        );

        return await HidFeatureWriteRetry.TryWriteAsync(
            buffer: m_buffer,
            cancellationToken: cancellationToken,
            device: m_device
        );
    }

    private void Compose(byte command, ReadOnlySpan<byte> payload) {
        m_buffer.AsSpan().Clear();
        m_buffer[0] = m_reportId;
        m_buffer[1] = command;
        m_buffer[2] = ((byte)payload.Length);
        payload.CopyTo(destination: m_buffer.AsSpan(start: 3));
    }
}
