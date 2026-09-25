using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// Both GPU hosts follow one device-loss policy (<see cref="DeviceLossRecovery"/>): a loss writes a
/// <c>[device-lost]</c> console line naming its reason, drains what it can, has the render tree release its device
/// resources (which refuses an armed capture as <see cref="CaptureRequestSlot.DeviceLostReason"/>), and then rebuilds
/// the device, retrying while it is absent within the reacquire budget. The windowed host rebuilds through its presenter
/// (<see cref="PresenterDeviceRebuild"/>), the offscreen host through the <see cref="IDeviceRebuild"/> its activation
/// registers, and a real offscreen host is run over a root whose first frame loses the device. A run that gives up releases
/// the tree and refuses the armed capture first, the same as one that recovers. No GPU is involved; the
/// clock the budget is measured on is manual.
/// </summary>
public sealed class DeviceLossRecoveryLawTests {
    private const long HungReason = unchecked((int)0x887A0006);

    [Fact]
    public async Task ALossDrainsReleasesTheTreeThenRebuildsAndRefusesTheArmedCapture() {
        var rig = new Rig();
        var request = rig.Root.Arm();

        var recovered = rig.Recovery.TryRecover(
            deviceLost: new DeviceLostException(
                message: "hung",
                reasonCode: HungReason
            ),
            rebuild: rig.Rebuild
        );

        Assert.True(condition: recovered);
        Assert.Equal(
            actual: rig.Log,
            expected: ["drain", "release", "rebuild"]
        );
        Assert.Equal(
            actual: Assert.Single(collection: rig.Lines),
            expected: "[device-lost] reason 0x887A0006; recovering (attempt 1/8)"
        );

        var refusal = Assert.IsType<DeviceLostException>(@object: (await request.Completion).Error);

        Assert.Equal(
            actual: refusal.Message,
            expected: CaptureRequestSlot.DeviceLostReason
        );
    }
    [Fact]
    public void AnAbsentDeviceIsRebuiltOnceItReturnsWithinTheBudget() {
        var rig = new Rig(absentRebuilds: 3);

        var recovered = rig.Recovery.TryRecover(
            deviceLost: new DeviceLostException(message: "removed"),
            rebuild: rig.Rebuild
        );

        Assert.True(condition: recovered);
        Assert.Equal(
            actual: rig.Rebuild.Calls,
            expected: 4
        );
        Assert.Equal(
            actual: rig.Time.Slept,
            expected: (3 * DeviceLossRecovery.ReacquireBackoff)
        );
    }
    [Fact]
    public void ADeviceThatDoesNotReturnWithinTheBudgetEndsTheRun() {
        var rig = new Rig(absentRebuilds: int.MaxValue);

        var recovered = rig.Recovery.TryRecover(
            deviceLost: new DeviceLostException(message: "removed"),
            rebuild: rig.Rebuild
        );

        Assert.False(condition: recovered);
        Assert.True(condition: (rig.Time.Slept >= DeviceLossRecovery.ReacquireBudget));
        Assert.True(condition: (rig.Time.Slept < (DeviceLossRecovery.ReacquireBudget + DeviceLossRecovery.ReacquireBackoff)));
    }
    [Fact]
    public void LossesWithNoFrameBetweenThemEndTheRunPastTheCapAndAFrameEndsTheStreak() {
        var rig = new Rig();

        for (var loss = 0; (loss < DeviceLossRecovery.MaxConsecutiveRecoveries); loss++) {
            Assert.True(condition: rig.Recovery.TryRecover(
                deviceLost: new DeviceLostException(message: "removed"),
                rebuild: rig.Rebuild
            ));
        }

        Assert.False(condition: rig.Recovery.TryRecover(
            deviceLost: new DeviceLostException(message: "removed"),
            rebuild: rig.Rebuild
        ));

        rig.Recovery.NoteFrameProduced();

        Assert.Equal(
            actual: rig.Recovery.Streak,
            expected: 0
        );
        Assert.True(condition: rig.Recovery.TryRecover(
            deviceLost: new DeviceLostException(message: "removed"),
            rebuild: rig.Rebuild
        ));
    }
    /// <summary>A host with nothing to rebuild through ends the run, but only after the tree is drained and released,
    /// so a capture armed at the loss is refused with the device-loss reason rather than left to end unserved or to meet
    /// a disposed tree.</summary>
    [Fact]
    public async Task AHostWithNothingToRebuildThroughReleasesTheTreeAndRefusesTheArmedCaptureBeforeEndingTheRun() {
        var rig = new Rig();
        var request = rig.Root.Arm();

        var recovered = rig.Recovery.TryRecover(
            deviceLost: new DeviceLostException(message: "removed"),
            rebuild: null
        );

        Assert.False(condition: recovered);
        Assert.Equal(
            actual: rig.Log,
            expected: ["drain", "release"]
        );
        _ = Assert.Single(collection: rig.Lines);
        Assert.Equal(
            actual: Assert.IsType<DeviceLostException>(@object: (await request.Completion).Error).Message,
            expected: CaptureRequestSlot.DeviceLostReason
        );
    }
    /// <summary>The loss past the consecutive cap ends the run the same way: drained, released and the armed capture
    /// refused, with no rebuild attempted.</summary>
    [Fact]
    public async Task TheLossPastTheCapReleasesTheTreeAndRefusesTheArmedCaptureWithoutARebuild() {
        var rig = new Rig();

        for (var loss = 0; (loss < DeviceLossRecovery.MaxConsecutiveRecoveries); loss++) {
            Assert.True(condition: rig.Recovery.TryRecover(
                deviceLost: new DeviceLostException(message: "removed"),
                rebuild: rig.Rebuild
            ));
        }

        var request = rig.Root.Arm();
        var rebuilds = rig.Rebuild.Calls;

        rig.Log.Clear();

        Assert.False(condition: rig.Recovery.TryRecover(
            deviceLost: new DeviceLostException(message: "removed"),
            rebuild: rig.Rebuild
        ));
        Assert.Equal(
            actual: rig.Log,
            expected: ["drain", "release"]
        );
        Assert.Equal(
            actual: rig.Rebuild.Calls,
            expected: rebuilds
        );
        Assert.Equal(
            actual: Assert.IsType<DeviceLostException>(@object: (await request.Completion).Error).Message,
            expected: CaptureRequestSlot.DeviceLostReason
        );
    }
    [Fact]
    public void TheWindowedHostRebuildsThroughItsPresenterOnTheWindowsSurface() {
        var rig = new Rig();
        var presenter = new RecoverablePresenter(log: rig.Log);

        var recovered = rig.Recovery.TryRecover(
            deviceLost: new DeviceLostException(message: "removed"),
            rebuild: new PresenterDeviceRebuild(
                Binding: default,
                Height: 600U,
                Presenter: presenter,
                Width: 800U
            )
        );

        Assert.True(condition: recovered);
        Assert.Equal(
            actual: rig.Log,
            expected: ["drain", "release", "recover 800x600"]
        );
    }
    [Fact]
    public async Task TheOffscreenHostRecoversThroughItsRegisteredRebuildAndProducesAgain() {
        var log = new List<string>();
        var root = new LosingRoot(log: log);
        var rebuild = new ScriptedRebuild(
            absentRebuilds: 0,
            log: log
        );
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings {
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(implementationInstance: new LauncherOptions {
            ExitAfter = TimeSpan.FromMilliseconds(value: 300),
        });
        builder.Services.AddSingleton(implementationInstance: new OffscreenRenderOptions(
            Height: 32U,
            Width: 32U
        ));
        builder.Services.AddSingleton<IRenderNode>(implementationInstance: root);
        builder.Services.AddSingleton<IDeviceRebuild>(implementationInstance: rebuild);
        builder.Services.AddLauncherOffscreenTerminal();

        using var host = builder.Build();
        var pump = host.Services.GetServices<IHostedService>().OfType<OffscreenTickHostedService>().Single();

        await WindowedHostFixture.RunAsync(host: host);

        Assert.False(condition: pump.ExecuteTask!.IsFaulted);
        Assert.Equal(
            actual: log.Take(count: 2),
            expected: ["release", "rebuild"]
        );
        Assert.Equal(
            actual: rebuild.Calls,
            expected: 1
        );
        Assert.True(condition: (root.FramesAfterLoss > 0));

        var refusal = Assert.IsType<DeviceLostException>(@object: (await root.Request.Completion).Error);

        Assert.Equal(
            actual: refusal.Message,
            expected: CaptureRequestSlot.DeviceLostReason
        );
    }

    /// <summary>The operator's <c>gpu.faults lose 2</c>: the offscreen host counts each frame against the faults, the
    /// second loses the device on a healthy root, and the host recovers through its registered rebuild exactly as from
    /// a real loss, refusing the capture armed at it, and renders on.</summary>
    [Fact]
    public async Task AnArmedLossLosesTheDeviceOnItsFrameAndTheOffscreenHostRecovers() {
        var log = new List<string>();
        var root = new ArmedRoot(log: log);
        var request = root.Arm();
        var rebuild = new ScriptedRebuild(
            absentRebuilds: 0,
            log: log
        );
        var faults = new GpuCreationFaults();
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings {
            DisableDefaults = true,
        });

        faults.ArmLoss(nth: 2);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(implementationInstance: new LauncherOptions {
            ExitAfter = TimeSpan.FromMilliseconds(value: 300),
        });
        builder.Services.AddSingleton(implementationInstance: new OffscreenRenderOptions(
            Height: 32U,
            Width: 32U
        ));
        builder.Services.AddSingleton<IRenderNode>(implementationInstance: root);
        builder.Services.AddSingleton<IDeviceRebuild>(implementationInstance: rebuild);
        builder.Services.AddSingleton(implementationInstance: faults);
        builder.Services.AddLauncherOffscreenTerminal();

        using var host = builder.Build();
        var pump = host.Services.GetServices<IHostedService>().OfType<OffscreenTickHostedService>().Single();

        await WindowedHostFixture.RunAsync(host: host);

        Assert.False(condition: pump.ExecuteTask!.IsFaulted);
        Assert.Equal(
            actual: log,
            expected: ["release", "rebuild"]
        );
        Assert.False(condition: faults.TryGetArmedLoss(remaining: out _));
        Assert.True(condition: (faults.FramesSeen > 2L));
        Assert.Equal(
            actual: Assert.IsType<DeviceLostException>(@object: (await request.Completion).Error).Message,
            expected: CaptureRequestSlot.DeviceLostReason
        );
    }

    // A policy over recording fakes: a published device whose drain is logged, a render root holding a capture slot,
    // a rebuild that reports the device absent a set number of times, and a manual clock that each backoff advances.
    private sealed class Rig {
        public Rig(int absentRebuilds = 0) {
            Log = [];
            Lines = [];
            Root = new ArmedRoot(log: Log);
            Rebuild = new ScriptedRebuild(
                absentRebuilds: absentRebuilds,
                log: Log
            );
            Time = new ManualTime();
            Recovery = new DeviceLossRecovery(
                logger: NullLogger.Instance,
                root: Root,
                rootHostContext: new DeviceHostContext(device: new DrainLoggingDevice(log: Log)),
                sleep: Time.Sleep,
                time: Time,
                writeLine: Lines.Add
            );
        }

        public List<string> Lines { get; }
        public List<string> Log { get; }
        public ScriptedRebuild Rebuild { get; }
        public DeviceLossRecovery Recovery { get; }
        public ArmedRoot Root { get; }
        public ManualTime Time { get; }
    }
    private sealed class ManualTime : TimeProvider {
        private long m_now;

        public TimeSpan Slept => TimeSpan.FromTicks(value: m_now);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => m_now;
        public void Sleep(TimeSpan duration) => m_now += duration.Ticks;
    }
    private sealed class ScriptedRebuild(int absentRebuilds, List<string> log) : IDeviceRebuild {
        private int m_absent = absentRebuilds;

        public int Calls { get; private set; }

        public void Rebuild() {
            Calls++;
            log.Add(item: "rebuild");

            if (m_absent > 0) {
                m_absent--;

                throw new DeviceLostException(message: "the adapter is still absent");
            }
        }
    }
    private sealed class RecoverablePresenter(List<string> log) : IDeviceLostRecoverable {
        public void RecoverFromDeviceLoss(NativeSurfaceBinding binding, uint width, uint height) =>
            log.Add(item: $"recover {width}x{height}");
    }
    // A render root holding one capture slot, as a capture-capable node does, and refusing it on a loss.
    private sealed class ArmedRoot(List<string> log) : IRenderNode {
        private readonly CaptureRequestSlot m_slot = new();

        public NodeDescriptor Descriptor { get; } = new(
            Name: "device-loss-recovery",
            SurfaceId: SurfaceId.New()
        );

        public FrameCaptureRequest Arm() {
            var request = new FrameCaptureRequest(path: "capture.png");

            m_slot.Arm(
                pendingPath: null,
                request: request
            );

            return request;
        }
        public void Dispose() { }
        public void OnDeviceLost() {
            log.Add(item: "release");
            m_slot.RefuseForDeviceLoss();
        }
        public Surface ProduceFrame(in FrameContext context) => default;
    }
    // An offscreen render root whose first frame loses the device with a capture armed; every later frame is produced.
    private sealed class LosingRoot : IRenderNode {
        private readonly List<string> m_log;

        private readonly CaptureRequestSlot m_slot = new();

        private bool m_lost;

        public LosingRoot(List<string> log) {
            m_log = log;
            Request = new FrameCaptureRequest(path: "capture.png");
            m_slot.Arm(
                pendingPath: null,
                request: Request
            );
        }

        public NodeDescriptor Descriptor { get; } = new(
            Name: "device-loss-offscreen",
            SurfaceId: SurfaceId.New()
        );

        public int FramesAfterLoss { get; private set; }
        public FrameCaptureRequest Request { get; }

        public void Dispose() { }
        public void OnDeviceLost() {
            m_log.Add(item: "release");
            m_slot.RefuseForDeviceLoss();
        }
        public Surface ProduceFrame(in FrameContext context) {
            if (!m_lost) {
                m_lost = true;

                throw new DeviceLostException(message: "the first frame lost the device");
            }

            FramesAfterLoss++;

            return default;
        }
    }
    private sealed class DeviceHostContext(IGpuDeviceContext device) : IHostContext {
        public bool HoldsCapability<TCapability>(out TCapability capability) where TCapability : class {
            capability = null!;

            return false;
        }
        public bool TryResolveCapability<TCapability>(out TCapability capability) where TCapability : class {
            capability = ((device as TCapability)!);

            return (capability is not null);
        }
    }
    private sealed class DrainLoggingDevice(List<string> log) : IGpuDeviceContext {
        public long AdapterLuid => 0L;
        public GpuDeviceCapabilities? Capabilities => null;
        public GpuDeviceIdentity? Identity => null;
        public GpuMemoryProfile MemoryProfile => default;
        public GpuDeviceServices Services => throw new NotSupportedException();

        // A removed device's drain throws, which the policy tolerates.
        public void WaitIdle() {
            log.Add(item: "drain");

            throw new DeviceLostException(message: "the device is removed");
        }
    }
}
