using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Puck.Abstractions.Gpu;
using Puck.Bench;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Scene;
using Puck.SdfVm;
using Puck.Vulkan.Presentation;

namespace Puck.Demo.Bench;

/// <summary>
/// Wires the engine benchmark harness to the demo AFTER the DI graph is built (the same post-hoc
/// <see cref="IHostedService"/> pattern <c>OverworldControlGateInstaller</c> uses): it attaches the neutral
/// seams (a GPU pass-timing source, the CPU frame-timing hub, the feature-switch registry, a console line submitter),
/// registers the §4 feature-switch ROSTER (each descriptor's delegates closing over the object that owns the lever —
/// engines never self-register), registers the §5 standard SUITE of scenes, and consumes the headless
/// <see cref="BenchBootRequest"/> handshake. Everything is registered eagerly here; the frame source (built lazily on
/// the overworld node's first frame) is resolved LAZILY inside the delegates and controllers, so by the time any bench
/// run's per-frame work executes it is built. A non-overworld root registers nothing — the harness degrades to an
/// empty suite that refuses loudly.
/// </summary>
internal sealed class BenchInstaller : IHostedService {
    private readonly BenchRuntime m_bench;
    private readonly FeatureSwitchRegistry m_switches;
    private readonly FrameTimingHub m_frameTiming;
    private readonly PresentPacingControl m_presentPacing;
    private readonly IRenderNode m_root;
    private readonly TextCommandSource m_textSource;
    private readonly ITerminalControl m_terminal;
    private readonly IServiceProvider m_services;
    // The frame-source LATCH (§4): a Set on a frame-source-backed switch issued BEFORE the node's first ProduceFrame
    // builds the frame source stores the request here (reporting success) instead of dropping it against a null source;
    // the first published frame flushes every latched value onto the now-built source (FlushLatchedSwitches), and Set/Get
    // then talk to the source directly. m_frameBackedApply holds each frame-backed switch's apply delegate for the flush;
    // it is written only during StartAsync (before any frame) so the flush reads it without a lock. The latch map itself
    // is guarded because HostFeatureApplier writes it from the DI-startup thread while the render thread flushes.
    private readonly Dictionary<string, string> m_latched = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, Func<OverworldFrameSource, string, bool>> m_frameBackedApply = new(comparer: StringComparer.Ordinal);
    private readonly object m_latchGate = new();
    private int m_headlessStopped;
    private int m_hostInfoCaptured;

    /// <summary>Creates the installer over the composed harness, control-plane registry, timing hub, present pacing,
    /// the render-node root, the stdin/console text source, the terminal-control baton (the clean-exit signal), and the
    /// service provider.</summary>
    public BenchInstaller(BenchRuntime bench, FeatureSwitchRegistry switches, FrameTimingHub frameTiming, PresentPacingControl presentPacing, IRenderNode root, TextCommandSource textSource, ITerminalControl terminal, IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(argument: bench);
        ArgumentNullException.ThrowIfNull(argument: switches);
        ArgumentNullException.ThrowIfNull(argument: frameTiming);
        ArgumentNullException.ThrowIfNull(argument: presentPacing);
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: textSource);
        ArgumentNullException.ThrowIfNull(argument: terminal);
        ArgumentNullException.ThrowIfNull(argument: services);

        m_bench = bench;
        m_switches = switches;
        m_frameTiming = frameTiming;
        m_presentPacing = presentPacing;
        m_root = root;
        m_textSource = textSource;
        m_terminal = terminal;
        m_services = services;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) {
        // Only the overworld root exposes the frame source (the bench-takeover composition point) and the per-pass
        // timing forwarder. A non-overworld root leaves the suite empty — bench.run then refuses loudly.
        if (m_root is not ICreatorModeHost host) {
            return Task.CompletedTask;
        }

        // The lazy frame-source resolver every frame-source-dependent delegate/controller closes over: null until the
        // node's first ProduceFrame builds it, by which point every bench run has already started producing frames.
        var frameSource = () => host.CreatorFrameSource;

        // Attach the neutral seams. The timing source is a thin adapter forwarding to the node's per-pass timing
        // passthrough (the SdfEngineNode is built lazily inside the node and never exposed to DI) — non-null so
        // bench.run's refusal gate passes; if the producer genuinely cannot read timestamps, the runner aborts loudly
        // at the first warmup scene (its no-timestamps refusal), never reporting zeros.
        m_bench.AttachTimingSource(source: new NodePassTimingSource(host: host));
        m_bench.AttachFrameTiming(hub: m_frameTiming);
        m_bench.AttachSwitches(registry: m_switches);
        m_bench.AttachConsole(submitLine: line => m_textSource.Enqueue(line: line));

        // Present-cadence honesty (§6): an official score comes only from the UNCAPPED twin. The report is stamped
        // `paced` iff the swapchain present MODE blocks to the refresh (vsync/fifo). The headless twin boots
        // presentMode "immediate" ⇒ not paced ⇒ `capped:false`; an in-session bench.run under the default vsync stamps
        // `paced:true` and prints the CAPPED banner. Resolved once, before any run scores (the switches only reach the
        // present RATE, never the boot-time MODE, so this is the honest source).
        m_bench.AssumePacedPresent = IsCappedPresentMode(services: m_services);

        // Host facts (§8's engine/host blocks) are captured on the FIRST published frame, not here: the GPU device the
        // run measured on is not selected until the launcher's window loop produces its first frame, so reading its
        // name (or the render/present tiers off the not-yet-built frame source) at StartAsync would be premature. The
        // report is written at end-of-run — long after the first frame — so the one-shot capture always lands in time.
        m_frameTiming.Published += CaptureHostInfoOnce;

        RegisterSwitches(frameSource: frameSource);
        RegisterScenes(frameSource: frameSource);
        ConsumeBootRequest();

        return Task.CompletedTask;
    }

    // ---- §8 host-info capture (one-shot, on the first published frame) --------------------------------------------
    // Resolves the engine/host facts a composition root actually knows — GPU name + backend (from the live Vulkan
    // renderer, once initialized), resolution + present mode (from HostSettings), the live present/render tiers (off
    // the switch registry), and the git commit/branch (a demo-side `git` process — the harness itself never shells
    // out) — and stamps them into every subsequent report. Fires on the render thread; runs its body exactly once.
    private void CaptureHostInfoOnce(FrameTimingSample sample) {
        // Wait for the node's first ProduceFrame to actually BUILD the frame source before firing: the GPU device, the
        // live render/present tiers, and the latch flush below all need it. Stays subscribed (no one-shot consumed) until
        // the source exists; the report is written at end-of-run, long after, so this always lands in time.
        if ((m_root as ICreatorModeHost)?.CreatorFrameSource is not { } frameSource) {
            return;
        }

        if (Interlocked.Exchange(location1: ref m_hostInfoCaptured, value: 1) != 0) {
            return;
        }

        m_frameTiming.Published -= CaptureHostInfoOnce;

        // Flush any host.features / feature.set overrides that LATCHED while the frame source was still building, BEFORE
        // reading the tiers below — so the report's render-scale tier (and the run itself) reflect the requested values,
        // not the pre-latch defaults.
        FlushLatchedSwitches(frameSource: frameSource);

        var settings = m_services.GetService<HostSettings>();

        var (gpuName, backend) = ResolveGpu(services: m_services, hostBackendIsDirectX: (settings?.HostBackendIsDirectX ?? false));
        var (gitCommit, gitBranch) = ResolveGit();

        m_bench.AttachHostInfo(hostInfo: BenchHostInfo.Detect() with {
            Backend = backend,
            GitBranch = gitBranch,
            GitCommit = gitCommit,
            GpuName = gpuName,
            PresentMode = (settings?.PresentMode ?? BenchHostInfo.Unknown),
            PresentRateTier = SwitchValue(name: "present.rate"),
            RenderScaleTier = SwitchValue(name: "render.scale"),
            ResolutionHeight = (int)(settings?.Height ?? 0U),
            ResolutionWidth = (int)(settings?.Width ?? 0U),
        });
    }

    // Whether the boot-time swapchain present MODE blocks to the display refresh (the only capped mode is vsync/fifo;
    // immediate/mailbox/adaptive all run uncapped). HostSettings carries the resolved raw token.
    private static bool IsCappedPresentMode(IServiceProvider services) =>
        string.Equals(a: services.GetService<HostSettings>()?.PresentMode, b: "vsync", comparisonType: StringComparison.OrdinalIgnoreCase);

    // Resolves the GPU name + backend token from the live renderer. The Vulkan renderer (the reference backend this
    // arc) reports its selected physical device's name once initialized; Direct3D 12 hosting reports its backend token
    // with an unknown name (the D3D12 validation pass is a deferred arc — §10). Any failure degrades to unknown, never
    // throws a report.
    private static (string GpuName, string Backend) ResolveGpu(IServiceProvider services, bool hostBackendIsDirectX) {
        try {
            if ((services.GetService<VulkanRenderer>() is { } renderer) && (services.GetService<Puck.Vulkan.Interfaces.IVulkanPhysicalDeviceApi>() is { } deviceApi)) {
                var name = deviceApi.GetDeviceName(instanceHandle: renderer.Instance.Handle, physicalDeviceHandle: renderer.PhysicalDevice.Handle);

                return (name, "vulkan");
            }
        } catch (InvalidOperationException) {
            // The renderer is not yet (or no longer) initialized — fall through to the backend-token-only result.
        }

        return (BenchHostInfo.Unknown, (hostBackendIsDirectX ? "d3d12" : "vulkan"));
    }

    // Reads a switch's live value through the registry (the tiers the run measured under), or unknown when absent.
    private string SwitchValue(string name) =>
        (m_switches.TryGet(name: name, descriptor: out var descriptor) ? descriptor.Get() : BenchHostInfo.Unknown);

    // The demo-side git identity (§8) — a short-lived `git` process, tolerant of any failure (detached build, no git on
    // PATH) with an unknown fallback. Puck.Bench NEVER shells out; the demo passes what it knows.
    private static (string Commit, string Branch) ResolveGit() =>
        (RunGit(arguments: "rev-parse --short HEAD"), RunGit(arguments: "rev-parse --abbrev-ref HEAD"));
    private static string RunGit(string arguments) {
        try {
            using var process = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = arguments,
                CreateNoWindow = true,
                FileName = "git",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });

            if (process is null) {
                return BenchHostInfo.Unknown;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();

            if (!process.WaitForExit(milliseconds: 2000)) {
                return BenchHostInfo.Unknown;
            }

            return (((process.ExitCode == 0) && (output.Length > 0)) ? output : BenchHostInfo.Unknown);
        } catch (Exception exception) when (((exception is SystemException) || (exception is InvalidOperationException))) {
            return BenchHostInfo.Unknown;
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ---- §4 the switch roster --------------------------------------------------------------------------------------
    private void RegisterSwitches(Func<OverworldFrameSource?> frameSource) {
        // Promoted existing levers.
        // render.scale — the world render-scale quality tier (the proven live SetRenderScaleTier plumbing). Frame-backed:
        // a boot-time host.features override latches until the frame source is built.
        AddFrameBacked(
            frameSource: frameSource,
            name: "render.scale", description: "The revealed-room render-scale quality tier.", category: "render",
            kind: FeatureSwitchKind.EnumTier, defaultValue: WorldRenderScaleTiers.Name(tier: WorldRenderScaleTier.Native),
            allowed: WorldRenderScaleTiers.Names,
            liveGet: static fs => fs.RenderScaleTierName,
            apply: static (fs, value) => {
                if (!WorldRenderScaleTiers.TryParse(name: value, tier: out _)) {
                    return false;
                }

                _ = fs.SetRenderScaleTier(name: value);

                return true;
            }
        );
        // present.rate — the window pacer's display-aware cadence (PresentPacingControl, a DI singleton — always resolvable).
        Add(
            name: "present.rate", description: "The window pacer's target present cadence.", category: "presentation",
            kind: FeatureSwitchKind.EnumTier, defaultValue: PresentRateTiers.NameForHertz(targetHertz: m_presentPacing.TargetHertz),
            allowed: PresentRateTiers.Names,
            get: () => PresentRateTiers.NameForHertz(targetHertz: m_presentPacing.TargetHertz),
            set: value => {
                if (!PresentRateTiers.TryParse(name: value, tier: out var tier)) {
                    return false;
                }

                m_presentPacing.SetTargetHertz(targetHertz: PresentRateTiers.TargetHertz(tier: tier));

                return true;
            }
        );
        // gpu.timing — the live GPU-timing arming control (the bench harness also arms/restores it around a run itself).
        // Disarming timing WHILE A RUN IS ACTIVE is REJECTED: the runner advances only on published frames and the
        // launcher publishes only while armed, so a mid-run disarm would freeze the state machine with no recovery. The
        // reject surfaces through feature.set's existing "rejected" path; the harness's own arm/restore bypasses this
        // guard (it calls GpuTimingControl.SetArmed directly, never the switch).
        Add(
            name: "gpu.timing", description: "Whether per-pass GPU timestamp capture is armed (disarming is refused while a bench run is active).", category: "gpu",
            kind: FeatureSwitchKind.FrameFlag, defaultValue: OnOff(on: GpuTimingControl.Shared.Armed), allowed: OnOffValues,
            get: () => OnOff(on: GpuTimingControl.Shared.Armed),
            set: value => {
                switch (value) {
                    case "on":
                        GpuTimingControl.Shared.SetArmed(armed: true);

                        return true;
                    case "off":
                        if (m_bench.IsRunning) {
                            return false;
                        }

                        GpuTimingControl.Shared.SetArmed(armed: false);

                        return true;
                    default:
                        return false;
                }
            }
        );
        // gpu.ray-query — boot-only (no live consumer): registered read-only, every live write rejected.
        var bootRayQuery = (DiegeticUiInstaller.ResolveTimingToggles(services: m_services).RayQuery ?? false);

        Add(
            name: "gpu.ray-query", description: "Whether the ray-query pipeline was built (BOOT-ONLY — rejects live writes).", category: "gpu",
            kind: FeatureSwitchKind.RebuildRequired, defaultValue: OnOff(on: bootRayQuery), allowed: OnOffValues,
            get: () => OnOff(on: bootRayQuery),
            set: _ => false
        );

        // sdf.shadow-cull — the existing SdfFrame.DisableShadowCull lane (on = the grid cull is enabled).
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.shadow-cull", description: "Whether the soft-shadow grid cull is enabled (off = the flat all-instances reference).", category: "sdf",
            kind: FeatureSwitchKind.FrameFlag, defaultValue: OnOff(on: true), allowed: OnOffValues,
            liveGet: static fs => OnOff(on: fs.SdfDebug.ShadowCull),
            apply: static (fs, value) => TrySetFlag(value: value, apply: on => fs.SdfDebug.SetShadowCull(on: on))
        );
        // sdf.normals — analytic dual (default) vs the 4-tap finite-difference probe (SdfFrame.UseFiniteDifferenceNormals).
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.normals", description: "The surface-normal probe: analytic dual or 4-tap finite difference.", category: "sdf",
            kind: FeatureSwitchKind.EnumTier, defaultValue: "analytic", allowed: ["analytic", "finite-diff"],
            liveGet: static fs => (fs.SdfDebug.UseFiniteDifferenceNormals ? "finite-diff" : "analytic"),
            apply: static (fs, value) => {
                switch (value) {
                    case "analytic": fs.SdfDebug.SetFiniteDifferenceNormals(useTaps: false); return true;
                    case "finite-diff": fs.SdfDebug.SetFiniteDifferenceNormals(useTaps: true); return true;
                    default: return false;
                }
            }
        );
        // The FOUR new shader-level toggles (SdfFrame.DisableSoftShadows / DisableAmbientOcclusion / ShadowDistanceScale /
        // DisableScreenLights), each backed by the demo-side live state the frame source threads into every frame.
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.soft-shadows", description: "Whether the sun's soft-shadow march runs (off = the sun goes unshadowed).", category: "sdf",
            kind: FeatureSwitchKind.FrameFlag, defaultValue: OnOff(on: true), allowed: OnOffValues,
            liveGet: static fs => OnOff(on: !fs.BenchDisableSoftShadows),
            apply: static (fs, value) => TrySetFlag(value: value, apply: on => fs.BenchDisableSoftShadows = !on)
        );
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.ao", description: "Whether ambient occlusion runs (off = occlusion forced to 1).", category: "sdf",
            kind: FeatureSwitchKind.FrameFlag, defaultValue: OnOff(on: true), allowed: OnOffValues,
            liveGet: static fs => OnOff(on: !fs.BenchDisableAmbientOcclusion),
            apply: static (fs, value) => TrySetFlag(value: value, apply: on => fs.BenchDisableAmbientOcclusion = !on)
        );
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.shadow-distance", description: "The soft-shadow reach scale (full/half/quarter).", category: "sdf",
            kind: FeatureSwitchKind.EnumTier, defaultValue: "full", allowed: ["full", "half", "quarter"],
            liveGet: static fs => ShadowDistanceName(scale: fs.BenchShadowDistanceScale),
            apply: static (fs, value) => {
                switch (value) {
                    case "full": fs.BenchShadowDistanceScale = 0f; return true;
                    case "half": fs.BenchShadowDistanceScale = 0.5f; return true;
                    case "quarter": fs.BenchShadowDistanceScale = 0.25f; return true;
                    default: return false;
                }
            }
        );
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.screen-lights", description: "Whether the diegetic CRTs spill light into the room.", category: "sdf",
            kind: FeatureSwitchKind.FrameFlag, defaultValue: OnOff(on: true), allowed: OnOffValues,
            liveGet: static fs => OnOff(on: !fs.BenchDisableScreenLights),
            apply: static (fs, value) => TrySetFlag(value: value, apply: on => fs.BenchDisableScreenLights = !on)
        );
        // sdf.shadow-proxy (PATH B) — shadow rays skip Subtraction-family carve instances and march the pre-carve union
        // hull, collapsing the O(cluster) soft-shadow re-march the carves/storm frame is bound on. DEFAULT OFF (the owner
        // flips defaults): an unset frame uploads 0 and is byte-identical. Conservative when ON (carve cavities stop
        // letting sun through — shadows darker, never light-leaked).
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.shadow-proxy", description: "Whether shadow rays skip carve (subtraction) instances and march the pre-carve union hull (off = the full occluder set).", category: "sdf",
            kind: FeatureSwitchKind.FrameFlag, defaultValue: OnOff(on: false), allowed: OnOffValues,
            liveGet: static fs => OnOff(on: fs.BenchEnableShadowProxy),
            apply: static (fs, value) => TrySetFlag(value: value, apply: on => fs.BenchEnableShadowProxy = on)
        );
        // sdf.cadence-gate (perf plan Phase 6.1) — a presentation-only frame-graph optimization: when a frame's
        // render-consumed inputs are byte-identical to the last rendered frame, the engine SKIPS the mask/beam/cull-args/
        // views passes and re-composites from the retained views output (a camera ease counts as a change; a bound live
        // screen source or in-progress carve bake forces a render). DEFAULT OFF (the owner flips defaults): an unset
        // frame renders fully and is byte-identical.
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.cadence-gate", description: "Whether an unchanged frame skips the render passes and re-composites (off = every frame renders fully).", category: "sdf",
            kind: FeatureSwitchKind.FrameFlag, defaultValue: OnOff(on: false), allowed: OnOffValues,
            liveGet: static fs => OnOff(on: fs.BenchEnableCadenceGate),
            apply: static (fs, value) => TrySetFlag(value: value, apply: on => fs.BenchEnableCadenceGate = on)
        );
        // sdf.far-bound (perf plan Phase 5.1, F1) — the beam-published per-tile far bound: the depth past which a tile's
        // cone cannot produce any footprint-accepted hit, so the fine march exits early on empty-sky rays. DEFAULT ON
        // (output-identical to a full march); OFF marches to MaxDistance exactly as pre-F1 — the "off" side of the owner's
        // far-field A/B isolator.
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.far-bound", description: "Whether the beam-published per-tile far bound exits empty-sky rays early (off = the fine march runs to MaxDistance as pre-F1).", category: "sdf",
            kind: FeatureSwitchKind.FrameFlag, defaultValue: OnOff(on: true), allowed: OnOffValues,
            liveGet: static fs => OnOff(on: fs.BenchFarBound),
            apply: static (fs, value) => TrySetFlag(value: value, apply: on => fs.BenchFarBound = on)
        );
        // sdf.shadow-far-exit (perf plan Phase 5.1, F2) — softShadow's no-further-darkening light-side early exit: it
        // returns the running result once the remaining reach provably cannot lower it under the field's along-ray
        // 1-Lipschitz bound. DEFAULT ON (a march-path change, not bit-identical); OFF runs the full shadow budget/reach
        // exactly as pre-F2 — the "off" side of the owner's shadow far-exit A/B isolator.
        AddFrameBacked(
            frameSource: frameSource,
            name: "sdf.shadow-far-exit", description: "Whether the soft-shadow march exits once it provably cannot darken further (off = the full shadow budget/reach as pre-F2).", category: "sdf",
            kind: FeatureSwitchKind.FrameFlag, defaultValue: OnOff(on: true), allowed: OnOffValues,
            liveGet: static fs => OnOff(on: fs.BenchShadowFarExit),
            apply: static (fs, value) => TrySetFlag(value: value, apply: on => fs.BenchShadowFarExit = on)
        );
        // sdf.carve-bake — settled carve clusters bake into a sampled distance-field brick composed as one ordinary
        // subtraction, so primary, shadow, and AO marches avoid O(carve-count) work. SdfCarveBakePlanner.Enabled is
        // process-wide because the interactive debug scene and benchmark workload use separate planner instances.
        Add(
            name: "sdf.carve-bake", description: "Whether settled carve clusters bake into a sampled distance-field brick (off = always analytic).", category: "sdf",
            kind: FeatureSwitchKind.RebuildRequired, defaultValue: OnOff(on: SdfCarveBakePlanner.Enabled), allowed: OnOffValues,
            get: () => OnOff(on: SdfCarveBakePlanner.Enabled),
            set: value => TrySetFlag(value: value, apply: on => SdfCarveBakePlanner.Enabled = on)
        );
        // sdf.grid-cull — DEVIATION: registered BOOT-ONLY-REJECT. The overworld room's instance-grid cull is built ON by
        // SdfCompositionFrameSource, which exposes no live buildInstanceGrid toggle; threading one into the room's
        // composed program is a Puck.SdfVm change out of this wave's scope. Reports the production state ("on") and
        // rejects every live write, exactly like gpu.ray-query.
        Add(
            name: "sdf.grid-cull", description: "Whether the room packs the uniform-grid instance cull (BOOT-ONLY — rejects live writes).", category: "sdf",
            kind: FeatureSwitchKind.RebuildRequired, defaultValue: OnOff(on: true), allowed: OnOffValues,
            get: () => OnOff(on: true),
            set: _ => false
        );
    }

    // ---- §5 the standard suite -------------------------------------------------------------------------------------
    private void RegisterScenes(Func<OverworldFrameSource?> frameSource) {
        // 0: warmup — the storm rung, weight 0 (reported, never scored): spins DVFS clocks to their plateau first.
        m_bench.RegisterScene(descriptor: new BenchSceneDescriptor(
            Category: "warmup", Controller: new SyntheticBenchScene(frameSource: frameSource, workload: SyntheticBenchWorkloads.Storm()),
            Description: "DVFS warmup (the storm rung) — reported, never scored.", Name: "warmup",
            SampleFrames: 300, WarmFrames: 0, Weight: 0.0
        ));
        // 1: room.flythrough — a scripted 6-waypoint dolly through the revealed room (zero cabinets).
        m_bench.RegisterScene(descriptor: new BenchSceneDescriptor(
            Category: "world", Controller: new FlythroughBenchScene(frameSource: frameSource, warmFrames: 60, sampleFrames: 900),
            Description: "A scripted dolly through the revealed room (zero cabinets booted).", Name: "room.flythrough",
            SampleFrames: 900, WarmFrames: 60, Weight: 0.35
        ));
        // 2: room.active — the revealed room with four cabinets booted (world + 4 emulators + 4 lit CRTs + screen lights).
        m_bench.RegisterScene(descriptor: new BenchSceneDescriptor(
            Category: "world", Controller: new ActiveRoomBenchScene(frameSource: frameSource),
            Description: "The revealed room with four cabinets booted and emulating.", Name: "room.active",
            SampleFrames: 600, WarmFrames: 120, Weight: 0.35
        ));
        // 3-7: the synthetic feature rungs, weight 0.06 each — one fixed 1024-rung workload apiece.
        RegisterSynthetic(frameSource: frameSource, name: "sdf.shapes", description: "One fullscreen primitive (the shape-evaluation cost).", workload: SyntheticBenchWorkloads.Shapes());
        RegisterSynthetic(frameSource: frameSource, name: "sdf.ops", description: "A torus behind one point warp (marginal op cost).", workload: SyntheticBenchWorkloads.Ops());
        RegisterSynthetic(frameSource: frameSource, name: "sdf.carves", description: "1024 clustered subtraction carves (the views-cost worst case).", workload: SyntheticBenchWorkloads.Carves());
        RegisterSynthetic(frameSource: frameSource, name: "sdf.storm", description: "1024 dynamic instances (the always-tested beam list).", workload: SyntheticBenchWorkloads.Storm());
        RegisterSynthetic(frameSource: frameSource, name: "sdf.instances", description: "1024 grid-culled static instances.", workload: SyntheticBenchWorkloads.Instances());
    }
    private void RegisterSynthetic(Func<OverworldFrameSource?> frameSource, string name, string description, SyntheticBenchWorkloads.Workload workload) =>
        m_bench.RegisterScene(descriptor: new BenchSceneDescriptor(
            Category: "feature", Controller: new SyntheticBenchScene(frameSource: frameSource, workload: workload),
            Description: description, Name: name, SampleFrames: 300, WarmFrames: 60, Weight: 0.06
        ));

    // ---- §9 the headless --bench handshake -------------------------------------------------------------------------
    private void ConsumeBootRequest() {
        if (BenchBootRequest.Suite is not { } suite) {
            return;
        }

        if (BenchBootRequest.ExitWhenComplete) {
            // A clean scored run exits 0, an abort/refusal exits 1; exit once (RunCompleted fires per run/leg — for a
            // plain bench.run that is exactly once). Fires on the render thread, so we ask the TERMINAL to close (the
            // same baton --exit-after-seconds pulls) rather than calling StopApplication directly: RequestExit lets the
            // window loop break and tear its GPU tree down on its OWN thread before the host disposes the DI container,
            // avoiding the double-dispose race a mid-frame StopApplication triggers.
            m_bench.RunCompleted += outcome => {
                if (Interlocked.Exchange(location1: ref m_headlessStopped, value: 1) != 0) {
                    return;
                }

                Environment.ExitCode = (outcome.Succeeded ? 0 : 1);
                m_terminal.RequestExit();
            };
        }

        m_textSource.Enqueue(line: (BenchBootRequest.IncludeSamples ? $"bench.run {suite} samples" : $"bench.run {suite}"));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------
    private static readonly IReadOnlyList<string> OnOffValues = ["on", "off"];

    // Registers a FRAME-SOURCE-BACKED switch with LATCH semantics: while the frame source exists, Get/Set read/apply it
    // live; before the node's first frame builds it, Set stores the (validated) request in m_latched and reports success
    // — FlushLatchedSwitches applies every latched value the moment the source materializes — and Get reports the latched
    // value. Without this, a host.features / feature.set override issued at composition (before the first frame) would
    // hit a null source, be rejected, and never take effect (finding: host.features dead on arrival).
    private void AddFrameBacked(Func<OverworldFrameSource?> frameSource, string name, string description, string category, FeatureSwitchKind kind, string defaultValue, IReadOnlyList<string> allowed, Func<OverworldFrameSource, string> liveGet, Func<OverworldFrameSource, string, bool> apply) {
        m_frameBackedApply[name] = apply;

        Add(
            name: name, description: description, category: category, kind: kind, defaultValue: defaultValue, allowed: allowed,
            get: () => {
                if (frameSource() is { } fs) {
                    return liveGet(arg: fs);
                }

                lock (m_latchGate) {
                    return (m_latched.TryGetValue(key: name, value: out var latched) ? latched : defaultValue);
                }
            },
            set: value => {
                if (frameSource() is { } fs) {
                    return apply(arg1: fs, arg2: value);
                }

                // The source is not built yet — validate against the allowed set (the flush's apply would reject an
                // unknown value; a latch must not smuggle one past it) and store the request.
                if (!allowed.Contains(value: value, comparer: StringComparer.Ordinal)) {
                    return false;
                }

                lock (m_latchGate) {
                    // Close the race with FlushLatchedSwitches (render thread): if the flush drained the map and built
                    // the source between the null-check above and this lock, apply directly instead of latching into an
                    // already-flushed map (where the value would sit forever).
                    if (frameSource() is { } built) {
                        return apply(arg1: built, arg2: value);
                    }

                    m_latched[name] = value;
                }

                return true;
            }
        );
    }

    // Applies every latched frame-backed switch value onto the now-built frame source, then clears the map. Runs on the
    // render thread from the first-published-frame hook; the lock guards against a concurrent latch write from
    // HostFeatureApplier's DI-startup thread.
    private void FlushLatchedSwitches(OverworldFrameSource frameSource) {
        lock (m_latchGate) {
            foreach (var (name, value) in m_latched) {
                if (m_frameBackedApply.TryGetValue(key: name, value: out var apply)) {
                    _ = apply(arg1: frameSource, arg2: value);
                }
            }

            m_latched.Clear();
        }
    }
    private void Add(string name, string description, string category, FeatureSwitchKind kind, string defaultValue, IReadOnlyList<string> allowed, Func<string> get, Func<string, bool> set) =>
        m_switches.Register(descriptor: new FeatureSwitchDescriptor(
            AllowedValues: allowed, Category: category, DefaultValue: defaultValue, Description: description, Get: get, Kind: kind, Name: name, Set: set
        ));
    private static string OnOff(bool on) => (on ? "on" : "off");
    private static string ShadowDistanceName(float scale) =>
        ((scale == 0.5f) ? "half" : ((scale == 0.25f) ? "quarter" : "full"));

    // Applies an on/off flag directly (no frame source needed).
    private static bool TrySetFlag(string value, Action<bool> apply) {
        switch (value) {
            case "on": apply(obj: true); return true;
            case "off": apply(obj: false); return true;
            default: return false;
        }
    }

    /// <summary>The pass-timing adapter the harness reads through: it forwards to the overworld node's per-pass timing
    /// passthrough (<see cref="ICreatorModeHost.TryReadSdfPassTimings"/>), so the harness never needs the lazily-built
    /// <see cref="SdfEngineNode"/> instance itself. The labels/count are the node's STATIC pass metadata.</summary>
    private sealed class NodePassTimingSource : IPassTimingSource {
        private readonly ICreatorModeHost m_host;

        public NodePassTimingSource(ICreatorModeHost host) {
            m_host = host;
        }

        public int PassCount => SdfEngineNode.PassTimingCount;
        public ReadOnlySpan<string> PassLabels => SdfEngineNode.PassTimingLabels;

        public bool TryReadPassTimings(Span<double> passMilliseconds, out int passCount, out double frameMilliseconds) =>
            m_host.TryReadSdfPassTimings(passMilliseconds: passMilliseconds, passCount: out passCount, frame: out frameMilliseconds);
    }
}
