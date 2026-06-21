using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Gamepad;

/// <summary>
/// Drives controller rumble through GameInput (gameinput.dll), which actuates Xbox haptics over every
/// connection type — including the Wireless Adapter and Bluetooth, where the legacy <c>XInputSetState</c> path
/// is silently ignored — and reaches the trigger (impulse) motors. It enumerates real per-device handles via
/// <c>RegisterDeviceCallback</c> (the dictionary owns each handle's COM reference) so a caller can bind the
/// specific device for an XInput slot via <see cref="Bind"/> and rumble exactly that controller — the "current
/// reading" device is global and can't distinguish controllers.
/// </summary>
public sealed class GameInputHaptics : IDisposable
{
    private readonly HashSet<IGameInputDevice> m_bound = [];
    private readonly Action<string>? m_diagnostics;
    private readonly Dictionary<nint, IGameInputDevice> m_devices = [];
    private readonly object m_deviceLock = new();

    private ulong m_callbackToken;
    private GameInputDeviceCallback? m_deviceCallback;
    private IGameInput? m_gameInput;

    /// <summary>Initializes a new instance of the <see cref="GameInputHaptics"/> class.</summary>
    /// <param name="diagnostics">An optional diagnostics sink.</param>
    public GameInputHaptics(Action<string>? diagnostics = null) {
        m_diagnostics = diagnostics;
    }

    /// <summary>Creates the GameInput instance and registers for gamepad device connect/disconnect.</summary>
    /// <returns><see langword="true"/> if GameInput is ready; otherwise <see langword="false"/>.</returns>
    public bool TryInitialize() {
        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        try {
            if ((GameInputNative.GameInputCreate(gameInput: out var gameInput) < 0) || (gameInput is null)) {
                return false;
            }

            m_gameInput = gameInput;
            m_deviceCallback = OnDeviceStatusChanged;

            var status = gameInput.RegisterDeviceCallback(
                callbackFunc: Marshal.GetFunctionPointerForDelegate(d: m_deviceCallback),
                callbackToken: out m_callbackToken,
                context: nint.Zero,
                device: null,
                enumerationKind: GameInputNative.GameInputAsyncEnumeration,
                inputKind: GameInputNative.GameInputKindGamepad,
                statusFilter: GameInputNative.GameInputDeviceConnected
            );

            if (status < 0) {
                // Registration failed, so the device dictionary will stay empty forever and no correlation or
                // rumble can ever work. Report failure honestly so the manager logs the XInput-only fallback
                // instead of claiming GameInput haptics are ready.
                m_diagnostics?.Invoke($"[gameinput] RegisterDeviceCallback failed (hr=0x{status:x8}); GameInput haptics disabled");
                m_deviceCallback = null;
                m_gameInput = null;

                return false;
            }

            return true;
        }
        catch (DllNotFoundException) {
            return false;
        }
        catch (EntryPointNotFoundException) {
            return false;
        }
    }

    private void OnDeviceStatusChanged(ulong callbackToken, nint context, nint device, ulong timestamp, uint currentStatus, uint previousStatus) {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        try {
            var connected = ((currentStatus & GameInputNative.GameInputDeviceConnected) != 0u);

            lock (m_deviceLock) {
                if (connected) {
                    if (!m_devices.ContainsKey(key: device)) {
                        m_devices[device] = (IGameInputDevice)Marshal.GetObjectForIUnknown(pUnk: device);
                        m_diagnostics?.Invoke($"[gameinput] device connected (0x{device:x})");
                    }
                }
                else if (m_devices.Remove(key: device, value: out var removed)) {
                    // Drop the reference (the GC finalizes the RCW; explicitly releasing it would separate a
                    // handle a connection may still borrow) and free any binding so a slot can rebind.
                    _ = m_bound.Remove(item: removed);
                    m_diagnostics?.Invoke($"[gameinput] device disconnected (0x{device:x})");
                }
            }
        }
        catch {
            // This runs on a GameInput thread; an exception must never propagate into native code.
        }
    }

    /// <summary>
    /// Correlates a caller (an XInput slot) to its physical GameInput device by matching the buttons it currently
    /// holds, and reserves that device so no other slot can bind it. Returns a device only when EXACTLY ONE
    /// unbound device shows <paramref name="targetButtons"/> — if two pads hold the same mask the binding is
    /// deferred, so a transient ambiguity can never produce a stable mis-binding. The returned device is owned by
    /// this instance's dictionary; the caller borrows it and must <see cref="Unbind"/> it on release.
    /// </summary>
    /// <param name="targetButtons">The GameInput button bitmask the caller currently holds (must be non-zero).</param>
    /// <returns>The reserved device, or <see langword="null"/> if there is no unambiguous match.</returns>
    public IGameInputDevice? Bind(uint targetButtons) {
        var gameInput = m_gameInput;

        if (!OperatingSystem.IsWindows() || (gameInput is null) || (targetButtons == 0u)) {
            return null;
        }

        lock (m_deviceLock) {
            IGameInputDevice? match = null;

            foreach (var device in m_devices.Values) {
                if (m_bound.Contains(item: device)) {
                    continue;
                }

                if ((gameInput.GetCurrentReading(
                    device: device,
                    inputKind: GameInputNative.GameInputKindGamepad,
                    reading: out var reading
                ) < 0) || (reading is null)) {
                    continue;
                }

                try {
                    if (reading.GetGamepadState(state: out var gamepad) && (gamepad.Buttons == targetButtons)) {
                        if (match is not null) {
                            // A second unbound device holds the same mask — ambiguous. Defer until a frame where
                            // the masks disambiguate rather than risk a stable binding to the wrong controller.
                            return null;
                        }

                        match = device;
                    }
                }
                finally {
                    _ = Marshal.ReleaseComObject(o: reading);
                }
            }

            if (match is not null) {
                _ = m_bound.Add(item: match);
            }

            return match;
        }
    }

    /// <summary>Releases a device reserved by <see cref="Bind"/> so it can rebind to a future slot.</summary>
    /// <param name="device">The device to release.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public void Unbind(IGameInputDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        lock (m_deviceLock) {
            _ = m_bound.Remove(item: device);
        }
    }

    /// <summary>
    /// Writes the rumble state to a specific, previously-correlated device. Never throws on a disconnected
    /// device — a controller can vanish between binding and this write, and a propagating
    /// <see cref="COMException"/> would otherwise tear down the caller's poll loop and kill all rumble for the
    /// session.
    /// </summary>
    /// <param name="device">The slot's bound device.</param>
    /// <param name="rumbleParams">The four motor intensities to apply.</param>
    /// <returns>
    /// <see langword="true"/> if the write reached a live device; <see langword="false"/> if the device has
    /// disconnected (the caller should drop the stale binding and re-correlate).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public bool RumbleDevice(IGameInputDevice device, in GameInputRumbleParams rumbleParams) {
        ArgumentNullException.ThrowIfNull(device);

        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        // If the device disconnected since binding, the status callback has already removed it from the
        // dictionary; report a stale handle so the caller rebinds rather than writing into a dead RCW.
        lock (m_deviceLock) {
            if (!m_devices.ContainsValue(value: device)) {
                return false;
            }
        }

        try {
            device.SetRumbleState(rumbleParams: in rumbleParams);

            return true;
        }
        catch (COMException) {
            return false;
        }
        catch (InvalidComObjectException) {
            return false;
        }
    }

    public void Dispose() {
        if (!OperatingSystem.IsWindows()) {
            m_gameInput = null;

            return;
        }

        var gameInput = m_gameInput;

        if (gameInput is not null) {
            if (m_callbackToken != 0ul) {
                _ = gameInput.UnregisterCallback(callbackToken: m_callbackToken, timeoutInMicroseconds: 1_000_000ul);
            }

            // Drop references and let the GC/finalizers release the COM objects. Explicitly releasing the device
            // RCWs would separate handles still borrowed by connections and crash on use.
            lock (m_deviceLock) {
                m_bound.Clear();
                m_devices.Clear();
            }
        }

        m_deviceCallback = null;
        m_gameInput = null;
    }
}
