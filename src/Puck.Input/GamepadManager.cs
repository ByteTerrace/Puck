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
    private const ushort VendorNintendo = 1406;
    private const ushort VendorSony = 1356;
    private const ushort VendorValve = 10462;          // 0x28DE
    private const ushort ProductSwitchPro = 8201;      // 0x2009 (same over USB and Bluetooth)
    private const ushort ProductDualSense = 3302;      // 0x0CE6 (same over USB and Bluetooth)
    private const ushort ProductSteamControllerWired = 4354;   // 0x1102 (wired)
    private const ushort ProductSteamControllerDongle = 4418;  // 0x1142 (wireless receiver; up to four slots)
    private const ushort ProductSteamControllerTriton = 4868;  // 0x1304 (2026 "Triton" pad, over its wireless puck receiver)
    // The Steam Controller exposes its controller state on a vendor-defined HID collection (usage page 0xFF00,
    // usage 0x01), not a generic-desktop gamepad one, so it needs its own gate below. The receiver additionally
    // exposes a management collection (usage 0x02) that carries no controller input and must not be opened.
    private const ushort VendorUsagePage = 0xFF00;
    private const ushort SteamControllerUsage = 0x0001;

    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(value: 1.5);
    private readonly IGamepadAcquisitionSource? m_acquisitionSource;
    private readonly IInputClock m_clock;
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
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="hidSource"/> or <paramref name="clock"/> is
    /// <see langword="null"/>.</exception>
    public GamepadManager(
        IHidDeviceSource hidSource,
        IInputClock clock,
        IGamepadAcquisitionSource? acquisitionSource = null,
        Action<string>? diagnostics = null
    ) {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hidSource);

        m_acquisitionSource = acquisitionSource;
        m_clock = clock;
        m_diagnostics = diagnostics;
        m_hidSource = hidSource;
        m_registry = new Registry(owner: this);
    }

    private static bool IsKnownVendor(ushort vendorId) {
        return (vendorId is VendorMicrosoft or VendorSony or VendorNintendo or VendorValve);
    }
    // A multi-slot wireless receiver exposes one vendor input collection per pairing slot, most of which may sit
    // empty indefinitely. Those open DORMANT — no player slot, exempt from the streaming watchdog — and claim a
    // player slot only when a controller actually streams through them. Opening them eagerly instead makes every
    // empty slot flap forever (open → 5 s liveness fault → prune → reopen on the next rescan) while burning real
    // player slots on phantoms.
    private static bool IsDeferredActivationProduct(ushort vendorId, ushort productId) {
        return ((VendorValve == vendorId)
            && (productId is ProductSteamControllerDongle or ProductSteamControllerTriton));
    }
    private static bool IsSupportedSteamController(ushort productId) {
        return (productId is ProductSteamControllerWired or ProductSteamControllerDongle);
    }
    // The Steam Controller's input arrives on a vendor-defined collection that the generic gamepad gate rejects;
    // accept it explicitly (a supported product's usage-0x01 vendor collection), and only it — the receiver's
    // usage-0x02 management collection and the lizard keyboard/mouse collections are correctly left out.
    private static bool IsSteamControllerInputInterface(IHidDevice hid) {
        return ((VendorValve == hid.VendorId)
            && IsSupportedSteamController(productId: hid.ProductId)
            && (VendorUsagePage == hid.UsagePage)
            && (SteamControllerUsage == hid.Usage));
    }
    // The 2026 "Triton" pad also arrives on the vendor input collection, but speaks a different control framing
    // (feature reports under report id 1) and a richer input report, so it has its own parser and gate.
    private static bool IsSteamControllerTritonInputInterface(IHidDevice hid) {
        return ((VendorValve == hid.VendorId)
            && (ProductSteamControllerTriton == hid.ProductId)
            && (VendorUsagePage == hid.UsagePage)
            && (SteamControllerUsage == hid.Usage));
    }
    private static IGamepadParser? CreateParser(IHidDevice hid) {
        // Switch Pro and DualSense are wired up over HID; Xbox flows through the acquisition source, not here.
        if ((VendorNintendo == hid.VendorId) && (ProductSwitchPro == hid.ProductId)) {
            return new NintendoSwitchController(device: hid);
        }

        if ((VendorSony == hid.VendorId) && (ProductDualSense == hid.ProductId)) {
            return new DualSenseController(device: hid);
        }

        if (IsSteamControllerInputInterface(hid: hid)) {
            return new SteamController(device: hid);
        }

        if (IsSteamControllerTritonInputInterface(hid: hid)) {
            return new SteamControllerTriton(device: hid);
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
                // input-bearing one is opened. Standard pads use the generic-desktop gamepad/joystick collection;
                // the Steam Controller uses a vendor collection, gated separately. Reject the rest so they never
                // claim a player slot or a duplicate identity for the same physical device.
                if (!IsGamepadCollection(hid: hid) && !IsSteamControllerInputInterface(hid: hid) && !IsSteamControllerTritonInputInterface(hid: hid)) {
                    // A Valve device on a vendor collection that isn't an input-bearing Steam Controller interface
                    // is its receiver's management/secondary collection (usage 0x02) or a non-input variant — call
                    // that out distinctly from the generic "not a gamepad collection" skip so bring-up isn't misleading.
                    if ((VendorValve == candidate.VendorId) && (VendorUsagePage == hid.UsagePage)) {
                        m_diagnostics?.Invoke($"[gamepad] Valve product=0x{candidate.ProductId:x4} vendor collection (usage 0x{hid.UsagePage:x4}/0x{hid.Usage:x4}) carries no controller input (management/secondary collection); skipping");
                    } else {
                        m_diagnostics?.Invoke($"[gamepad] vendor=0x{candidate.VendorId:x4} product=0x{candidate.ProductId:x4} skipped (usage 0x{hid.UsagePage:x2}/0x{hid.Usage:x2}, not a gamepad collection)");
                    }

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

                var deferActivation = IsDeferredActivationProduct(vendorId: candidate.VendorId, productId: candidate.ProductId);
                var playerIndex = (deferActivation ? -1 : AllocatePlayerSlotLocked());
                var device = new GamepadDevice(
                    activateOnStream: deferActivation,
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
                m_diagnostics?.Invoke((deferActivation
                    ? $"[gamepad] opened {parser.Type} receiver slot dormant ({candidate.Transport.ToString().ToLowerInvariant()} vendor=0x{candidate.VendorId:x4} product=0x{candidate.ProductId:x4}, {device.DeviceId}); a player slot is claimed when it streams"
                    : $"[gamepad] opened {parser.Type} as player {(playerIndex + 1)} ({candidate.Transport.ToString().ToLowerInvariant()} vendor=0x{candidate.VendorId:x4} product=0x{candidate.ProductId:x4}, {device.DeviceId})"));
            }

            ReconcileDeferredActivationLocked();
        }

        foreach (var device in faulted) {
            device.Dispose();
        }
    }
    // Deferred-activation devices (wireless-receiver slots) hold a player slot only while a controller actually
    // streams through them: assign the lowest free slot when streaming starts, release it when the receiver
    // reports the slot empty again. Runs under the gate, from both the rescan and the per-frame drain (so
    // activation latency is one frame, not one rescan).
    private void ReconcileDeferredActivationLocked() {
        foreach (var connection in m_devices) {
            if ((connection is not GamepadDevice device) || !device.ActivateOnStream || device.IsFaulted) {
                continue;
            }

            if (device.HasStream && (device.PlayerIndex < 0)) {
                device.AssignPlayerSlot(playerIndex: AllocatePlayerSlotLocked());
                m_diagnostics?.Invoke($"[gamepad] {device.Type} paired as player {(device.PlayerIndex + 1)} ({device.DeviceId})");
            } else if (!device.HasStream && (device.PlayerIndex >= 0)) {
                device.ReleasePlayerSlot();
                m_diagnostics?.Invoke($"[gamepad] {device.Type} unpaired; player slot released ({device.DeviceId})");
            }
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
            // Promote a receiver slot that just started streaming (and demote one whose pad left) before folding
            // contributions, so its very first drained data already carries a player identity.
            ReconcileDeferredActivationLocked();

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

                // A dormant receiver slot has nothing behind it — never drain it (its coalescer may still hold the
                // departed controller's final state, which must not replay into the command pipeline).
                if (device.PlayerIndex < 0) {
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
            var ids = new List<InputDeviceId>(capacity: m_devices.Count);

            foreach (var device in m_devices) {
                // A dormant receiver slot has no controller behind it; it stays invisible until it streams.
                if (device.PlayerIndex >= 0) {
                    ids.Add(item: device.DeviceId);
                }
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
                // Skip faulted entries so a stale duplicate (same id, pre-pruning) can't shadow the live device,
                // and dormant receiver slots (PlayerIndex < 0) — nothing is behind them to resolve.
                if ((device.DeviceId == deviceId) && !device.IsFaulted && (device.PlayerIndex >= 0)) {
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
                if ((device.DeviceId == deviceId) && !device.IsFaulted && (device.PlayerIndex >= 0)) {
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
                if ((device.DeviceId == deviceId) && !device.IsFaulted && (device.PlayerIndex >= 0)) {
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
                if ((device.DeviceId == deviceId) && !device.IsFaulted && (device.PlayerIndex >= 0)) {
                    capabilities = device.InputCapabilities;

                    return true;
                }
            }
        }

        capabilities = GamepadInputCapabilities.None;

        return false;
    }
    /// <summary>Stops acquisition and releases the connected gamepad resources.</summary>
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

            m_owner.m_diagnostics?.Invoke($"[gamepad] opened {connection.Type} as player {(connection.PlayerIndex + 1)} ({connection.DeviceId})");

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
