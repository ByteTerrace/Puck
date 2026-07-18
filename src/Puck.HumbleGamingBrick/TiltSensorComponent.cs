using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The default <see cref="ITiltSensor"/>: motionless (the fixed centered reading <c>0x81D0</c>, bit-identical to the
/// MBC7's pre-seam behavior) until a host records a tilt sample through <see cref="SetTilt"/> — the recorded value
/// then stays constant until the next call, exactly like <see cref="JoypadComponent"/>'s held buttons, so a mid-segment
/// accelerometer latch replays bit-identically. Snapshottable: a mid-frame restore must reproduce whatever reading is
/// currently held, the same reasoning that makes the joypad's buttons snapshot state.
/// </summary>
public sealed class TiltSensorComponent : ITiltSensor, ISnapshotable {
    // The centered reading a motionless cartridge latches; the hardware centers near this, not at 0x8000 (mirrors
    // Mbc7Cartridge.AccelerometerCenter).
    private const int Center = 0x81D0;
    // The raw-unit swing one full-deflection host sample (-1 or 1) maps to.
    private const int Range = 0x0700;

    private int m_x = Center;
    private int m_y = Center;

    /// <inheritdoc/>
    public void Read(out int x, out int y) {
        x = m_x;
        y = m_y;
    }
    /// <inheritdoc/>
    public void SetTilt(float x, float y) {
        m_x = (Center + (int)(Math.Clamp(value: x, min: -1f, max: 1f) * Range));
        m_y = (Center - (int)(Math.Clamp(value: y, min: -1f, max: 1f) * Range));
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteInt32(value: m_x);
        writer.WriteInt32(value: m_y);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_x = reader.ReadInt32();
        m_y = reader.ReadInt32();
    }
}
