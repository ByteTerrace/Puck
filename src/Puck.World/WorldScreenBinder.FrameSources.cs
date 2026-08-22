using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // Standalone Capture sources declared through DeclareFrameSource, keyed by the source record itself (WorldFrameSource
    // equality — Camera/View/Probe already have a stable identity elsewhere: EnsureCameraFeed keys by sensor,
    // RegisterCameraView by camera name, GetOrAddProbeFeed by probe id; a Capture carries no such name, so the record IS
    // its own key). Populated only on success — a source the platform cannot ever open (no window-capture support at
    // all) records no entry and is retried the next time DeclareFrameSource sees it (cheap: HUD structure rebuilds run
    // at most once per definition revision plus once per edited identity).
    private readonly Dictionary<WorldFrameSource, CaptureFeed> m_frameCaptures = new();
    // HUD frame sources are generation-owned. Exact (source, enclosing-seat) references preserve the seat fallback
    // identity, while resource-specific release below folds aliases that resolve the same capture, camera, or view.
    // Counts make the binder safe for more than one overlay owner without coupling either owner to the other's
    // generation cadence.
    private readonly Dictionary<(WorldFrameSource Source, int Seat), int> m_frameSourceReferences = new();
    // Probe camera inputs have their own persistent lane. A HUD generation can therefore narrow or remove only its
    // contribution without weakening a kernel graph that still consumes the same sensor.
    private readonly Dictionary<(int Seat, WorldCameraSensor Sensor), List<WorldFeedProfile>> m_probeCameraDemand = new();

    /// <summary>Declares that a <see cref="WorldFrameSource"/> is consumed outside any declared <c>screens</c> row (a
    /// HUD/overlay <c>Frame</c> element) and opens its feed through the same shared machinery a screen row would —
    /// idempotent, safe to call at boot or on every definition/identity revision. A camera source declares nothing
    /// here — its demand is a live set recomputed every publish from actual consumers (<see cref="ReconcileCameraDemand"/>),
    /// including <see cref="RetainFrameSource"/>'s reference table); a view
    /// camera renders every <see cref="ViewStack"/> refresh with no wired screen narrowing its round-robin turn
    /// (<see cref="RegisterCameraView"/>'s default <c>isLive</c> is always-true); a probe reads whatever its own
    /// kernel publishes; a capture opens through <see cref="TryCreateCaptureFeed"/>, the same ladder a declared
    /// screen's capture source uses.</summary>
    /// <param name="source">The frame source a HUD element (or any other non-screen consumer) names.</param>
    /// <param name="seat">The 1-based enclosing seat scope a camera source with no authored <c>Seat</c> resolves
    /// against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void DeclareFrameSource(WorldFrameSource source, int seat) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (m_disposed) {
            return;
        }

        switch (source) {
            case WorldScreenSource.Camera:
                // ReconcileCameraDemand derives demand straight from live consumer state every publish — nothing to
                // declare imperatively here.
                break;
            case WorldScreenSource.View view:
                if (ResolveCamera(name: view.CameraName) is { } resolvedCamera) {
                    RegisterCameraView(camera: resolvedCamera);
                }

                break;
            case WorldScreenSource.Probe probe:
                _ = GetOrAddProbeFeed(id: probe.Id);

                break;
            case WorldScreenSource.Capture capture:
                if (
                    !m_frameCaptures.ContainsKey(key: source) &&
                    (TryCreateCaptureFeed(capture: capture, fault: out _) is { } feed)
                ) {
                    m_frameCaptures[source] = feed;
                }

                break;
        }
    }
    /// <summary>Retains one non-screen consumer's use of a frame source. The first reference starts or registers its
    /// producer; later references share it.</summary>
    /// <param name="source">The source to retain.</param>
    /// <param name="seat">The enclosing 1-based seat fallback.</param>
    public void RetainFrameSource(WorldFrameSource source, int seat) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (m_disposed) {
            return;
        }

        var key = (source, seat);

        if (m_frameSourceReferences.TryGetValue(key: key, value: out var references)) {
            m_frameSourceReferences[key] = checked(references + 1);

            return;
        }

        m_frameSourceReferences[key] = 1;

        switch (source) {
            case WorldScreenSource.Camera:
                // This table's membership feeds ReconcileCameraDemand directly at the next publish — nothing to
                // open imperatively here.
                break;
            default:
                DeclareFrameSource(source: source, seat: seat);

                break;
        }
    }
    /// <summary>Releases one retained non-screen source. Its producer is torn down when this was the final reference
    /// and no screen or probe consumer still owns the same underlying resource.</summary>
    /// <param name="source">The source to release.</param>
    /// <param name="seat">The enclosing 1-based seat fallback used when retained.</param>
    public void ReleaseFrameSource(WorldFrameSource source, int seat) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var key = (source, seat);

        if (!m_frameSourceReferences.TryGetValue(key: key, value: out var references)) {
            return;
        }

        if (references > 1) {
            m_frameSourceReferences[key] = (references - 1);

            return;
        }

        _ = m_frameSourceReferences.Remove(key: key);

        switch (source) {
            case WorldScreenSource.Camera:
                // This table's membership drops out of ReconcileCameraDemand's next recompute — nothing to release
                // imperatively here.
                break;
            case WorldScreenSource.View view:
                ReleaseOrphanedCameraView(name: view.CameraName);

                break;
            case WorldScreenSource.Capture:
                if (!HasRetainedCapture(source: source) && m_frameCaptures.Remove(key: source, value: out var capture)) {
                    capture.Dispose();
                }

                break;
        }
    }
    private static WorldFeedProfile RichestProfile(WorldFeedProfile left, WorldFeedProfile right) => new(
        Width: Math.Max(val1: left.Width, val2: right.Width),
        Height: Math.Max(val1: left.Height, val2: right.Height),
        RefreshRateHz: Math.Max(val1: left.RefreshRateHz, val2: right.RefreshRateHz)
    );
    private void RetainProbeCameraDemandCore(WorldScreenSource.Camera camera, int contextSeat) {
        var key = (camera.Seat ?? contextSeat, camera.Sensor);
        var requested = (camera.Profile ?? WorldFeedProfile.Default);

        if (!m_probeCameraDemand.TryGetValue(key: key, value: out var demands)) {
            demands = [];
            m_probeCameraDemand.Add(key: key, value: demands);
        }

        demands.Add(item: requested);
    }
    private void ReleaseProbeCameraDemandCore(WorldScreenSource.Camera camera, int contextSeat) {
        var key = (camera.Seat ?? contextSeat, camera.Sensor);

        if (!m_probeCameraDemand.TryGetValue(key: key, value: out var demands)) {
            return;
        }

        var requested = (camera.Profile ?? WorldFeedProfile.Default);
        var index = demands.FindIndex(match: profile => Equals(objA: profile, objB: requested));

        if (index < 0) {
            return;
        }

        demands.RemoveAt(index: index);

        if (demands.Count == 0) {
            _ = m_probeCameraDemand.Remove(key: key);
        }
    }
    private bool HasRetainedCapture(WorldFrameSource source) {
        foreach (var entry in m_frameSourceReferences.Keys) {
            if (Equals(objA: entry.Source, objB: source)) {
                return true;
            }
        }

        return false;
    }
    private bool HasRetainedView(string cameraName) {
        foreach (var entry in m_frameSourceReferences.Keys) {
            if (
                (entry.Source is WorldScreenSource.View view) &&
                string.Equals(a: view.CameraName, b: cameraName, comparisonType: StringComparison.Ordinal)
            ) {
                return true;
            }
        }

        return false;
    }
    private static void MergeCameraDemand(
        Dictionary<(int Seat, WorldCameraSensor Sensor), WorldFeedProfile> demands,
        WorldScreenSource.Camera camera,
        int fallbackSeat
    ) {
        var key = (camera.Seat ?? fallbackSeat, camera.Sensor);
        var requested = (camera.Profile ?? WorldFeedProfile.Default);

        demands[key] = (demands.TryGetValue(key: key, value: out var existing)
            ? RichestProfile(left: existing, right: requested)
            : requested
        );
    }
    // Recomputes webcam demand from the live consumers — the screen slots whose current source is a camera, the
    // persistent probe declarations, and the actively retained HUD frame sources (m_frameSourceReferences) — then
    // syncs every device's open feeds to the result. Called once per publish (WorldScreenBinder.Camera.cs's
    // CaptureCamera) rather than only on a membership change: a seat's DEMANDED (sensor, profile) can stay fixed
    // while its RESOLVED device changes underneath it (player.assign moving a camera to a different seat), and only
    // ReconcileCameraFeedsToDemand's per-device pass (driven by the roster's current seating) observes that — so it
    // always runs, even when this method's rebuilt m_cameraDemand is byte-for-byte the same as last frame's. A
    // screen/probe/HUD change and a roster reassignment therefore both resolve within one produced frame with no
    // imperative "declare"/"undeclare" bookkeeping to keep in step (the one-publish seam). Zero-alloc: m_cameraDemand
    // itself is the reused buffer (cleared and refilled in place; it holds at most a handful of (seat, sensor) pairs).
    private void ReconcileCameraDemand() {
        m_cameraDemand.Clear();

        foreach (var slot in m_slots.Values) {
            if ((slot.CameraSeat is not { } seat) || (slot.CameraSensorKind is not { } sensor)) {
                continue;
            }

            var requested = (
                (slot.DeclaredSource is WorldScreenSource.Camera declared) &&
                ((declared.Seat ?? seat) == seat) &&
                (declared.Sensor == sensor)
            )
                ? (declared.Profile ?? WorldFeedProfile.Default)
                : WorldFeedProfile.Default;
            var key = (seat, sensor);

            m_cameraDemand[key] = (m_cameraDemand.TryGetValue(key: key, value: out var existing)
                ? RichestProfile(left: existing, right: requested)
                : requested
            );
        }

        foreach (var entry in m_probeCameraDemand) {
            foreach (var requested in entry.Value) {
                m_cameraDemand[entry.Key] = (m_cameraDemand.TryGetValue(key: entry.Key, value: out var existing)
                    ? RichestProfile(left: existing, right: requested)
                    : requested
                );
            }
        }

        foreach (var entry in m_frameSourceReferences.Keys) {
            if (entry.Source is WorldScreenSource.Camera camera) {
                MergeCameraDemand(demands: m_cameraDemand, camera: camera, fallbackSeat: entry.Seat);
            }
        }

        ReconcileCameraFeedsToDemand();
    }
    // Syncs every known device's open feeds to m_cameraDemand under the roster's CURRENT seating: a device's feed
    // whose (resolved seat, sensor) is no longer demanded is dropped (the device's graph then closes on the next
    // ServiceCameraDeviceGraph pass once it carries no feeds); a feed whose demanded profile changed is replaced;
    // every demand entry is then ensured against whichever device the roster resolves it to.
    private void ReconcileCameraFeedsToDemand() {
        foreach (var device in m_cameraDeviceOrder) {
            var seat = (m_roster.DeviceSlot(device: device.DeviceId) is { } slot ? (slot + 1) : 0);
            // A slot can carry more than one camera at once (TryGetSeatDevice's own most-recently-assigned
            // tie-break exists precisely because PlayerRoster allows this) — so mapping to a demanding seat is not
            // enough to keep a feed open. Only the seat's RESOLVED device (the one TryFulfillCameraDemand/
            // TryResolveCamera would themselves pick) may hold one; a co-habiting camera that lost the tie-break
            // (the previous winner, after a player.assign seats a newer device on the same slot) is pruned here
            // exactly like a camera the seat no longer demands at all.
            var isResolvedDevice = (
                (seat != 0) &&
                m_roster.TryGetSeatDevice(slot: (seat - 1), kind: InputDeviceKind.Camera, device: out var resolvedDeviceId) &&
                (resolvedDeviceId == device.DeviceId)
            );

            for (var index = (device.Feeds.Count - 1); (index >= 0); index--) {
                var feed = device.Feeds[index];
                var key = (device.DeviceId, feed.Sensor);

                if (!isResolvedDevice || !m_cameraDemand.TryGetValue(key: (seat, feed.Sensor), value: out var requested)) {
                    _ = m_cameraFeeds.Remove(key: key);
                    device.Feeds.RemoveAt(index: index);
                    feed.Dispose();
                    device.SensorsChanged = true;

                    continue;
                }

                if (requested == feed.Profile) {
                    continue;
                }

                var replacement = new CameraFeed(profile: requested, sensor: feed.Sensor, surface: new CpuSurfaceSource()) {
                    Fault = "camera opening",
                };

                feed.Dispose();
                device.Feeds[index] = replacement;
                m_cameraFeeds[key] = replacement;
                device.SensorsChanged = true;
            }
        }

        foreach (var (seat, sensor) in m_cameraDemand.Keys) {
            _ = TryFulfillCameraDemand(seat: seat, sensor: sensor);
        }
    }
    /// <summary>Acquires the current frame for a previously-declared <see cref="WorldFrameSource"/> — the render-thread,
    /// per-frame counterpart of <see cref="DeclareFrameSource"/>. Reads the same shared feed a screen slot naming the
    /// identical source would (a camera seat/sensor, a named view, a probe id, or a standalone capture), so a HUD
    /// frame and a diegetic screen filming the same source see the same image.</summary>
    /// <param name="source">The frame source to sample.</param>
    /// <param name="seat">The 1-based enclosing seat scope a camera source with no authored <c>Seat</c> resolves
    /// against.</param>
    /// <param name="frame">The acquired frame, set only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the source is live this frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public bool TryAcquireFrame(WorldFrameSource source, int seat, out SdfScreenSourceFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: source);

        switch (source) {
            case WorldScreenSource.Camera camera:
                if (TryResolveCamera(seat: (camera.Seat ?? seat), sensor: camera.Sensor, device: out _, feed: out var cameraFeed, fault: out _)) {
                    frame = cameraFeed!.AcquireFrame();

                    return (0 != frame.ImageViewHandle);
                }

                break;
            case WorldScreenSource.View view:
                if (m_viewStack is { } stack) {
                    var handle = stack.Resolve(name: view.CameraName);

                    frame = handle;

                    return (0 != handle);
                }

                break;
            case WorldScreenSource.Probe probe:
                if (m_probeFeeds.TryGetValue(key: probe.Id, value: out var probeFeed)) {
                    frame = probeFeed.AcquireFrame();

                    return (0 != frame.ImageViewHandle);
                }

                break;
            case WorldScreenSource.Capture:
                if (m_frameCaptures.TryGetValue(key: source, value: out var captureFeed)) {
                    var handle = captureFeed.Handle();

                    frame = handle;

                    return (0 != handle);
                }

                break;
        }

        frame = default;

        return false;
    }
    // Standalone captures ride the same per-frame pull cadence a slot-owned capture does, from Publish (below), and
    // the same device-lost/dispose sweeps every other feed this binder owns gets.
    private void PublishFrameCaptures(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        foreach (var feed in m_frameCaptures.Values) {
            if (feed.ShouldPull()) {
                CaptureWindow(
                    deviceContext: deviceContext,
                    feed: feed,
                    gpu: gpu
                );
            }
        }
    }
    private void DisposeFrameCaptures() {
        foreach (var feed in m_frameCaptures.Values) {
            feed.Dispose();
        }

        m_frameCaptures.Clear();
    }
    private void NotifyFrameCapturesDeviceLost() {
        foreach (var feed in m_frameCaptures.Values) {
            feed.NotifyDeviceLost();
        }
    }
}
