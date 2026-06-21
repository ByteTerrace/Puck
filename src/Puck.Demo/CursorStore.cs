using System.Diagnostics;
using System.Numerics;
using Puck.Commands;

namespace Puck.Demo;

/// <summary>
/// The demo's per-controller cursor state: each device that has produced cursor input owns a normalized screen
/// position (0..1, origin top-left), a color matched to its player indicator, and (for devices with an IMU) a
/// fused orientation drawn as an on-screen gauge. The input thread writes (touchpad sets an absolute position;
/// stick/tilt nudge it; the fused orientation updates each report) and the render thread snapshots, so all access
/// is gated. Entries that stop updating (a disconnected controller) are pruned so stale cursors/gauges disappear.
/// </summary>
internal sealed class CursorStore
{
    /// <summary>One controller's cursor, as handed to the renderer.</summary>
    /// <param name="Position">The normalized screen position, 0..1, origin top-left.</param>
    /// <param name="Color">The cursor color, 0..1 per channel.</param>
    /// <param name="Orientation">The fused controller orientation (identity until an IMU report arrives).</param>
    public readonly record struct Cursor(Vector2 Position, Vector3 Color, Quaternion Orientation);

    private static readonly Cursor Default = new(Position: new Vector2(x: 0.5f, y: 0.5f), Color: Vector3.One, Orientation: Quaternion.Identity);
    // A connected controller streams continuously; one that's gone this long without an update is pruned.
    private static readonly long StaleTicks = Stopwatch.Frequency;

    private readonly Dictionary<InputDeviceId, Entry> m_cursors = [];
    private readonly object m_gate = new();

    private struct Entry
    {
        public Cursor Cursor;
        public long UpdatedTicks;
    }

    /// <summary>Sets a device's cursor to an absolute position (the touchpad path), creating it if new.</summary>
    public void SetAbsolute(InputDeviceId deviceId, Vector2 position, Vector3 color) {
        lock (m_gate) {
            Write(deviceId: deviceId, cursor: Existing(deviceId: deviceId) with { Color = color, Position = Clamp(position: position) });
        }
    }

    /// <summary>Nudges a device's cursor by a relative delta (the stick/tilt path); a new cursor starts centered.</summary>
    public void ApplyNudge(InputDeviceId deviceId, Vector2 delta, Vector3 color) {
        lock (m_gate) {
            var existing = Existing(deviceId: deviceId);

            Write(deviceId: deviceId, cursor: existing with { Color = color, Position = Clamp(position: (existing.Position + delta)) });
        }
    }

    /// <summary>Updates a device's fused orientation (the IMU path); a new cursor starts centered.</summary>
    public void SetOrientation(InputDeviceId deviceId, Quaternion orientation, Vector3 color) {
        lock (m_gate) {
            Write(deviceId: deviceId, cursor: Existing(deviceId: deviceId) with { Color = color, Orientation = orientation });
        }
    }

    /// <summary>Copies the current (non-stale) cursors into <paramref name="destination"/>, pruning stale ones.</summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <returns>The number of cursors written.</returns>
    public int Snapshot(Span<Cursor> destination) {
        lock (m_gate) {
            var now = Stopwatch.GetTimestamp();
            var count = 0;

            foreach (var (deviceId, entry) in m_cursors) {
                if ((now - entry.UpdatedTicks) > StaleTicks) {
                    m_staleScratch.Add(item: deviceId);

                    continue;
                }

                if (count < destination.Length) {
                    destination[count++] = entry.Cursor;
                }
            }

            foreach (var deviceId in m_staleScratch) {
                _ = m_cursors.Remove(key: deviceId);
            }

            m_staleScratch.Clear();

            return count;
        }
    }

    private readonly List<InputDeviceId> m_staleScratch = [];

    private void Write(InputDeviceId deviceId, Cursor cursor) {
        m_cursors[deviceId] = new Entry { Cursor = cursor, UpdatedTicks = Stopwatch.GetTimestamp() };
    }
    private Cursor Existing(InputDeviceId deviceId) {
        return (m_cursors.TryGetValue(key: deviceId, value: out var entry) ? entry.Cursor : Default);
    }
    private static Vector2 Clamp(Vector2 position) {
        return new Vector2(
            x: Math.Clamp(value: position.X, max: 1f, min: 0f),
            y: Math.Clamp(value: position.Y, max: 1f, min: 0f)
        );
    }
}
