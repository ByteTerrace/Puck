using Puck.Commands;
using Puck.Input.Devices;
using Puck.Input.Hid;
using Puck.Input.Output;

namespace Puck.Input;

/// <summary>
/// Owns the set of connected controllers: it periodically rescans HID device interfaces (via an injected
/// <see cref="IHidDeviceSource"/>), opens supported controllers as they appear (hotplug), assigns each a stable
/// <see cref="InputDeviceId"/>, starts its I/O loop, prunes disconnected devices, exposes a per-frame
/// <see cref="Drain"/> for the command pipeline, and resolves a device's <see cref="IGamepadOutput"/> by id.
/// </summary>
/// <remarks>
/// The manager is platform-neutral. HID acquisition runs against the injected <see cref="IHidDeviceSource"/> (a
/// per-OS raw-HID transport). Transports the manager cannot run itself — notably the Windows-only XInput +
/// GameInput Xbox backend — are supplied as an <see cref="IGamepadAcquisitionSource"/> that owns its own thread
/// and publishes connections through the manager's registry.
/// </remarks>
public sealed class GamepadManager : IDisposable {
    private const ushort VendorMicrosoft = 1118;
    private const ushort VendorSony = 1356;
    private const ushort VendorNintendo = 1406;
    private const ushort ProductSwitchPro = 8201;      // 0x2009 (same over USB and Bluetooth)
    private const ushort ProductDualSense = 3302;      // 0x0CE6 (same over USB and Bluetooth)

    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(value: 1.5);
    private readonly IGamepadAcquisitionSource? m_acquisitionSource;
    private readonly IInputClock? m_clock;
    private readonly Action<string>? m_diagnostics;
    private readonly List<IGamepadConnection> m_devices = [];
    private readonly object m_gate = new();
    private readonly IHidDeviceSource m_hidSource;
    private readonly HashSet<string> m_rejectedPaths = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly Registry m_registry;
    private bool m_disposed;
    private CancellationTokenSource? m_rescanCancellation;
    private Task? m_rescanLoop;
    private bool m_started;

    /// <summary>Initializes a new instance of the <see cref="GamepadManager"/> class.</summary>
    /// <param name="hidSource">
    /// The HID transport used to enumerate and open controllers (e.g. the Windows transport in
    /// <c>Puck.Platform</c>). A transport with no support on the current OS simply finds no devices.
    /// </param>
    /// <param name="acquisitionSource">
    /// An optional non-HID acquisition backend that drives its own discovery and publishes connections through the
    /// manager (e.g. the Windows XInput + GameInput Xbox backend). Pass <see langword="null"/> for none.
    /// </param>
    /// <param name="diagnostics">
    /// An optional sink for human-readable lifecycle/diagnostic messages (device discovery, handshake, read
    /// errors). Useful for hardware bring-up; pass <see langword="null"/> to disable.
    /// </param>
    /// <param name="clock">
    /// The shared capture clock the HID I/O loops stamp each report's arrival from (sub-frame timing authority).
    /// Pass <see langword="null"/> to leave reports unstamped (arrival ticks zero); the snapshot capture then
    /// falls back to a per-frame stamp.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="hidSource"/> is <see langword="null"/>.</exception>
    public GamepadManager(
        IHidDeviceSource hidSource,
        IGamepadAcquisitionSource? acquisitionSource = null,
        Action<string>? diagnostics = null,
        IInputClock? clock = null
    ) {
        ArgumentNullException.ThrowIfNull(hidSource);

        m_acquisitionSource = acquisitionSource;
        m_clock = clock;
        m_diagnostics = diagnostics;
        m_hidSource = hidSource;
        m_registry = new Registry(owner: this);
    }

    private static bool IsKnownVendor(ushort vendorId) {
        return (vendorId is VendorMicrosoft or VendorSony or VendorNintendo);
    }
    private static IGamepadParser? CreateParser(IHidDevice hid) {
        // Switch Pro and DualSense are wired up over HID; Xbox flows through the acquisition source, not here.
        if ((VendorNintendo == hid.VendorId) && (ProductSwitchPro == hid.ProductId)) {
            return new NintendoSwitchController(device: hid);
        }

        if ((VendorSony == hid.VendorId) && (ProductDualSense == hid.ProductId)) {
            return new DualSenseController(device: hid);
        }

        return null;
    }

    /// <summary>
    /// Performs an initial scan for connected controllers, starts the background rescan loop that picks up
    /// hotplugged devices, and starts the optional acquisition source. Idempotent.
    /// </summary>
    public void Start() {
        lock (m_gate) {
            if (m_started || m_disposed) {
                return;
            }

            m_started = true;
        }

        Rescan();

        var cancellation = new CancellationTokenSource();

        lock (m_gate) {
            if (m_disposed) {
                cancellation.Dispose();

                return;
            }

            m_rescanCancellation = cancellation;
            m_rescanLoop = Task.Run(function: () => RescanLoopAsync(cancellationToken: cancellation.Token));

            // The acquisition source owns its own thread; starting it here (under the gate, after the disposed
            // check) mirrors how the HID rescan loop is launched and keeps start/stop ordering simple.
            m_acquisitionSource?.Start(registry: m_registry);
        }
    }

    private async Task RescanLoopAsync(CancellationToken cancellationToken) {
        try {
            using var timer = new PeriodicTimer(period: RescanInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken: cancellationToken)) {
                Rescan();
            }
        } catch (OperationCanceledException) {
            // Expected on disposal.
        }
    }

    // The enumeration is the costly part, so it runs outside the lock; only the cheap reconcile (prune faulted,
    // open newly-arrived supported devices) holds it.
    private void Rescan() {
        List<HidDeviceInfo> candidates;

        try {
            candidates = EnumerateControllerInterfaces();
        } catch (Exception exception) {
            m_diagnostics?.Invoke($"[gamepad] enumeration failed: {exception.GetType().Name}: {exception.Message}");

            return;
        }

        var faulted = new List<IGamepadConnection>();

        lock (m_gate) {
            if (m_disposed) {
                return;
            }

            // Forget rejected paths that have since unplugged so a re-plugged device is re-examined, and bound
            // the set's growth. Then prune faulted devices (disposed after the gate is released, below).
            ReconcileRejectedLocked(candidates: candidates);
            PruneFaultedLocked(disposed: faulted);

            foreach (var candidate in candidates) {
                // Skip devices we already track, and known-vendor devices we've already opened but can't drive
                // yet (e.g. a non-gamepad collection or an unsupported product), so we don't reopen them every scan.
                if (IsTrackedLocked(path: candidate.Path) || m_rejectedPaths.Contains(item: candidate.Path)) {
                    continue;
                }

                IHidDevice? hid;

                try {
                    hid = m_hidSource.Open(devicePath: candidate.Path);
                } catch {
                    hid = null;
                }

                if (hid is null) {
                    // Transient (e.g. another process briefly holds the handle) — retry on a later scan.
                    continue;
                }

                // A controller can expose several HID top-level collections (gamepad, audio, vendor); only the
                // gamepad/joystick one carries input. Reject the others so they never claim a player slot or a
                // duplicate identity for the same physical device.
                if (!IsGamepadCollection(hid: hid)) {
                    m_diagnostics?.Invoke($"[gamepad] vendor=0x{candidate.VendorId:x4} product=0x{candidate.ProductId:x4} skipped (usage 0x{hid.UsagePage:x2}/0x{hid.Usage:x2}, not a gamepad collection)");
                    hid.Dispose();
                    _ = m_rejectedPaths.Add(item: candidate.Path);

                    continue;
                }

                var parser = CreateParser(hid: hid);

                if (parser is null) {
                    m_diagnostics?.Invoke($"[gamepad] HID vendor=0x{candidate.VendorId:x4} product=0x{candidate.ProductId:x4} (no parser yet)");
                    hid.Dispose();
                    _ = m_rejectedPaths.Add(item: candidate.Path);

                    continue;
                }

                var playerIndex = AllocatePlayerSlotLocked();
                var device = new GamepadDevice(
                    // Content-addressed from the device path: the same physical port yields the same id across
                    // reconnects (and restarts), so a controller that briefly drops keeps its identity, while
                    // two identical controllers on different ports stay distinct.
                    clock: m_clock,
                    deviceId: InputDeviceId.FromKey(key: candidate.Path),
                    diagnostics: m_diagnostics,
                    hid: hid,
                    parser: parser,
                    playerIndex: playerIndex
                );

                m_devices.Add(item: device);
                device.Start();
                m_diagnostics?.Invoke($"[gamepad] opened {parser.Type} as player {playerIndex + 1} ({candidate.Transport.ToString().ToLowerInvariant()} vendor=0x{candidate.VendorId:x4} product=0x{candidate.ProductId:x4}, {device.DeviceId})");
            }
        }

        foreach (var device in faulted) {
            device.Dispose();
        }
    }
    private void ReconcileRejectedLocked(List<HidDeviceInfo> candidates) {
        if (m_rejectedPaths.Count == 0) {
            return;
        }

        var present = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates) {
            _ = present.Add(item: candidate.Path);
        }

        _ = m_rejectedPaths.RemoveWhere(match: path => !present.Contains(item: path));
    }
    private static bool IsGamepadCollection(IHidDevice hid) {
        const ushort GenericDesktopPage = 0x01;
        const ushort JoystickUsage = 0x04;
        const ushort GamepadUsage = 0x05;

        return ((hid.UsagePage == GenericDesktopPage) && ((hid.Usage == GamepadUsage) || (hid.Usage == JoystickUsage)));
    }
    private List<HidDeviceInfo> EnumerateControllerInterfaces() {
        var result = new List<HidDeviceInfo>();

        // Filter to controller vendors from the parsed path before opening anything, so we never open the
        // system's keyboards/mice just to identify them.
        foreach (var info in m_hidSource.EnumerateInterfaces()) {
            if (IsKnownVendor(vendorId: info.VendorId)) {
                result.Add(item: info);
            }
        }

        return result;
    }
    private int AllocatePlayerSlotLocked() {
        // The lowest slot not held by a currently-connected device, so a disconnect frees its slot and a
        // reconnecting controller reclaims the lowest available one.
        var slot = 0;

        while (UsesSlotLocked(slot: slot)) {
            ++slot;
        }

        return slot;
    }
    private bool UsesSlotLocked(int slot) {
        foreach (var device in m_devices) {
            if (device.PlayerIndex == slot) {
                return true;
            }
        }

        return false;
    }
    private bool IsTrackedLocked(string path) {
        foreach (var device in m_devices) {
            if (string.Equals(a: device.Key, b: path, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }
    // Removes faulted devices from the tracked set under the gate and hands them to the caller to Dispose AFTER
    // the gate is released (Dispose briefly joins a HID device's I/O loop, which must not block under the gate).
    private void PruneFaultedLocked(List<IGamepadConnection> disposed) {
        for (var index = (m_devices.Count - 1); (index >= 0); --index) {
            var device = m_devices[index];

            if (device.IsFaulted) {
                m_devices.RemoveAt(index: index);
                disposed.Add(item: device);
            }
        }
    }

    /// <summary>
    /// Collects each connected device's coalesced contribution for this frame into <paramref name="buffer"/>
    /// (cleared first). Call once per frame from the command pipeline.
    /// </summary>
    /// <param name="buffer">The reusable buffer that receives one entry per device with pending data.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    public void Drain(List<GamepadDrain> buffer) {
        ArgumentNullException.ThrowIfNull(buffer);

        buffer.Clear();

        List<IGamepadConnection>? faulted = null;

        lock (m_gate) {
            for (var index = (m_devices.Count - 1); (index >= 0); --index) {
                var device = m_devices[index];

                // Prune disconnected/faulted devices promptly (between rescans) so they stop replaying stale
                // state, but Dispose them AFTER releasing the gate: a HID device's Dispose briefly joins its I/O
                // loop, and blocking that join under the global gate would serialize the rescan and acquisition.
                if (device.IsFaulted) {
                    m_devices.RemoveAt(index: index);
                    (faulted ??= []).Add(item: device);

                    continue;
                }

                if (device.Coalescer.Drain(
                    gyro: out var gyro,
                    latest: out var latest,
                    pressed: out var pressed,
                    pressEdges: out var pressEdges,
                    released: out var released
                )) {
                    buffer.Add(item: new GamepadDrain(
                        DeviceId: device.DeviceId,
                        Gyro: gyro,
                        Latest: latest,
                        Pressed: pressed,
                        PressEdges: pressEdges,
                        Released: released
                    ));
                }
            }
        }

        if (faulted is not null) {
            foreach (var device in faulted) {
                device.Dispose();
            }
        }
    }

    /// <summary>Returns a snapshot of the currently connected device identifiers.</summary>
    /// <returns>The ids of all connected devices at the time of the call.</returns>
    public IReadOnlyList<InputDeviceId> ConnectedDevices() {
        lock (m_gate) {
            var ids = new InputDeviceId[m_devices.Count];

            for (var index = 0; (index < m_devices.Count); ++index) {
                ids[index] = m_devices[index].DeviceId;
            }

            return ids;
        }
    }

    /// <summary>Resolves the output (haptics/indicator) handle for a connected device.</summary>
    /// <param name="deviceId">The device to resolve.</param>
    /// <param name="output">The device's output handle when found.</param>
    /// <returns><see langword="true"/> if a matching connected device exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetOutput(InputDeviceId deviceId, out IGamepadOutput output) {
        lock (m_gate) {
            foreach (var device in m_devices) {
                // Skip faulted entries so a stale duplicate (same id, pre-pruning) can't shadow the live device.
                if ((device.DeviceId == deviceId) && !device.IsFaulted) {
                    output = device.Output;

                    return true;
                }
            }
        }

        output = null!;

        return false;
    }

    /// <summary>Resolves the zero-based player slot of a connected device (drives its indicator color).</summary>
    /// <param name="deviceId">The device to resolve.</param>
    /// <param name="playerIndex">The device's player slot when found.</param>
    /// <returns><see langword="true"/> if a matching connected device exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetPlayerIndex(InputDeviceId deviceId, out int playerIndex) {
        lock (m_gate) {
            foreach (var device in m_devices) {
                if ((device.DeviceId == deviceId) && !device.IsFaulted) {
                    playerIndex = device.PlayerIndex;

                    return true;
                }
            }
        }

        playerIndex = 0;

        return false;
    }

    /// <summary>Resolves the controller family of a connected device (drives per-family UI glyphs).</summary>
    /// <param name="deviceId">The device to resolve.</param>
    /// <param name="type">The device's controller family when found.</param>
    /// <returns><see langword="true"/> if a matching connected device exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetType(InputDeviceId deviceId, out GamepadType type) {
        lock (m_gate) {
            foreach (var device in m_devices) {
                if ((device.DeviceId == deviceId) && !device.IsFaulted) {
                    type = device.Type;

                    return true;
                }
            }
        }

        type = GamepadType.Unknown;

        return false;
    }

    /// <summary>Resolves the input capabilities (gyro, analog triggers) of a connected device.</summary>
    /// <param name="deviceId">The device to resolve.</param>
    /// <param name="capabilities">The device's input capabilities when found.</param>
    /// <returns><see langword="true"/> if a matching connected device exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetInputCapabilities(InputDeviceId deviceId, out GamepadInputCapabilities capabilities) {
        lock (m_gate) {
            foreach (var device in m_devices) {
                if ((device.DeviceId == deviceId) && !device.IsFaulted) {
                    capabilities = device.InputCapabilities;

                    return true;
                }
            }
        }

        capabilities = GamepadInputCapabilities.None;

        return false;
    }
    public void Dispose() {
        IGamepadAcquisitionSource? acquisitionSource;
        CancellationTokenSource? cancellation;
        Task? loop;

        lock (m_gate) {
            if (m_disposed) {
                return;
            }

            m_disposed = true;
            acquisitionSource = m_acquisitionSource;
            cancellation = m_rescanCancellation;
            loop = m_rescanLoop;
        }

        // Stop the producers first — the rescan loop and the external acquisition source (which joins its own poll
        // thread and disposes any backend resources) — so no thread adds or removes connections while we tear the
        // set down. Then snapshot and dispose every connection (the manager owns connection lifetime; the source
        // leaves its still-connected ones tracked for disposal here). Each device's own loop blocks briefly on
        // teardown, so dispose outside the lock to avoid stalling a concurrent frame-thread Drain.
        cancellation?.Cancel();

        try {
            loop?.Wait(timeout: TimeSpan.FromMilliseconds(value: 500));
        } catch (AggregateException) {
            // The rescan loop faulted or was canceled during teardown; nothing actionable here.
        }

        acquisitionSource?.Dispose();

        List<IGamepadConnection> devices;

        lock (m_gate) {
            devices = [.. m_devices];

            m_devices.Clear();
        }

        foreach (var device in devices) {
            device.Dispose();
        }

        cancellation?.Dispose();
    }

    // The surface handed to an acquisition source: it allocates a player slot, builds the connection, and tracks
    // it atomically under the gate, mirroring how the HID rescan opens and registers a device.
    private sealed class Registry : IGamepadConnectionRegistry {
        private readonly GamepadManager m_owner;

        public Registry(GamepadManager owner) {
            m_owner = owner;
        }

        public IGamepadConnection Register(Func<int, IGamepadConnection> connectionFactory) {
            ArgumentNullException.ThrowIfNull(connectionFactory);

            IGamepadConnection connection;

            lock (m_owner.m_gate) {
                var playerIndex = m_owner.AllocatePlayerSlotLocked();

                connection = connectionFactory(arg: playerIndex);
                m_owner.m_devices.Add(item: connection);
            }

            m_owner.m_diagnostics?.Invoke($"[gamepad] opened {connection.Type} as player {connection.PlayerIndex + 1} ({connection.DeviceId})");

            return connection;
        }
        public void Unregister(IGamepadConnection connection) {
            ArgumentNullException.ThrowIfNull(connection);

            lock (m_owner.m_gate) {
                _ = m_owner.m_devices.Remove(item: connection);
            }

            m_owner.m_diagnostics?.Invoke($"[gamepad] device removed ({connection.DeviceId})");
        }
    }
}
