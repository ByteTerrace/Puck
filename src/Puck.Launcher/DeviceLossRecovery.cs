using System.Globalization;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>Rebuilds a lost graphics device, and whatever the host holds on it, in place. The windowed host rebuilds
/// through its presenter and the window's surface binding; the offscreen host through the rebuild its GPU activation
/// registers, which holds the hidden window's surface binding on Vulkan and recreates the Direct3D 12 device with no
/// swap chain.</summary>
public interface IDeviceRebuild {
    /// <summary>Destroys the lost device and creates its replacement in place, so every holder of the published device
    /// context keeps a valid reference. Called on the pump thread, after the render tree has released its device
    /// resources.</summary>
    /// <exception cref="DeviceLostException">No device could be created yet; the recovery waits and calls again.</exception>
    void Rebuild();
}
/// <summary>The windowed host's <see cref="IDeviceRebuild"/>: its presenter recreates the device and its presentation
/// resources on the window's surface.</summary>
/// <param name="Presenter">The presenter that rebuilds the device.</param>
/// <param name="Binding">The window's surface binding the swap chain is re-bound to.</param>
/// <param name="Width">The surface width, in pixels.</param>
/// <param name="Height">The surface height, in pixels.</param>
public sealed record PresenterDeviceRebuild(IDeviceLostRecoverable Presenter, NativeSurfaceBinding Binding, uint Width, uint Height) : IDeviceRebuild {
    /// <inheritdoc/>
    public void Rebuild() =>
        Presenter.RecoverFromDeviceLoss(
            binding: Binding,
            height: Height,
            width: Width
        );
}
/// <summary>
/// The one device-loss policy both GPU hosts follow, on the pump thread, when a frame surfaces a
/// <see cref="DeviceLostException"/>: it writes a <see cref="LossLinePrefix"/> console line naming the reason, drains
/// what it can, has the render tree release its device resources (<see cref="IRenderNode.OnDeviceLost"/>, which also
/// fails every capture armed at the loss with <c>CaptureRequestSlot.DeviceLostReason</c>), and then rebuilds the device
/// through an <see cref="IDeviceRebuild"/>, retrying every <see cref="ReacquireBackoff"/> while the adapter is absent,
/// for up to <see cref="ReacquireBudget"/>. The fixed-step simulation is never touched. A host gives up when there is
/// nothing to rebuild through, when the device does not return within the budget, or when
/// <see cref="MaxConsecutiveRecoveries"/> losses follow one another with no frame produced between them; it has drained
/// and released the tree, refusing every armed capture, before it gives up, the same as when it recovers.
/// </summary>
public sealed class DeviceLossRecovery {
    /// <summary>The prefix of the console line written to standard error for each loss: <c>[device-lost] reason
    /// 0x…; recovering (attempt n/max)</c>, the reason being the low 32 bits of
    /// <see cref="DeviceLostException.ReasonCode"/> (a Direct3D 12 removal reason, or a Vulkan result).</summary>
    public const string LossLinePrefix = "[device-lost]";
    /// <summary>The most losses recovered from with no frame produced between them before the run gives up, so a
    /// permanently dead device fails the run rather than spinning.</summary>
    public const int MaxConsecutiveRecoveries = 8;

    /// <summary>The wait between rebuild attempts while the adapter is absent.</summary>
    public static readonly TimeSpan ReacquireBackoff = TimeSpan.FromMilliseconds(value: 250);
    /// <summary>How long one loss's recovery waits for the device to return. A full adapter removal can leave the
    /// driver unable to reinitialize in this process at all, so the wait is bounded.</summary>
    public static readonly TimeSpan ReacquireBudget = TimeSpan.FromSeconds(value: 10);

    private readonly ILogger m_logger;
    private readonly IRenderNode m_root;
    private readonly IHostContext m_rootHostContext;
    private readonly Action<TimeSpan> m_sleep;
    private readonly TimeProvider m_time;
    private readonly Action<string> m_writeLine;

    private int m_streak;

    /// <summary>Initializes a new instance of the <see cref="DeviceLossRecovery"/> class for one host run.</summary>
    /// <param name="logger">The host's logger, which records the reason's detail and the recovery's outcome.</param>
    /// <param name="root">The render root whose tree releases its device resources.</param>
    /// <param name="rootHostContext">The root host context whose published device context is drained.</param>
    /// <param name="writeLine">Writes one console line; the host passes its console output.</param>
    /// <param name="time">The clock the reacquire budget is measured on; the system clock when <see langword="null"/>.</param>
    /// <param name="sleep">Waits out one backoff; <see cref="Thread.Sleep(TimeSpan)"/> when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/>, <paramref name="root"/>,
    /// <paramref name="rootHostContext"/> or <paramref name="writeLine"/> is <see langword="null"/>.</exception>
    public DeviceLossRecovery(ILogger logger, IRenderNode root, IHostContext rootHostContext, Action<string> writeLine, TimeProvider? time = null, Action<TimeSpan>? sleep = null) {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHostContext);
        ArgumentNullException.ThrowIfNull(writeLine);

        m_logger = logger;
        m_root = root;
        m_rootHostContext = rootHostContext;
        m_sleep = (sleep ?? Thread.Sleep);
        m_time = (time ?? TimeProvider.System);
        m_writeLine = writeLine;
    }

    /// <summary>Gets the number of losses recovered from, or being recovered from, since the last produced
    /// frame.</summary>
    public int Streak => m_streak;

    /// <summary>Records a frame produced on the device, which ends the current streak of losses.</summary>
    public void NoteFrameProduced() {
        if (0 == m_streak) {
            return;
        }

        m_logger.LogInformation(
            message: "Graphics device recovered; rendering resumed after {Attempts} attempt(s).",
            m_streak
        );
        m_streak = 0;
    }
    /// <summary>Recovers from one device loss (see the class summary for the order).</summary>
    /// <param name="deviceLost">The loss the frame surfaced.</param>
    /// <param name="rebuild">The host's way to rebuild the device, or <see langword="null"/> when it has none.</param>
    /// <returns><see langword="true"/> when the device was rebuilt and the host renders on; <see langword="false"/> when
    /// the host must end the run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deviceLost"/> is <see langword="null"/>.</exception>
    public bool TryRecover(DeviceLostException deviceLost, IDeviceRebuild? rebuild) {
        ArgumentNullException.ThrowIfNull(deviceLost);

        ++m_streak;
        m_writeLine(string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{LossLinePrefix} reason 0x{unchecked((uint)deviceLost.ReasonCode):X8}; recovering (attempt {m_streak}/{MaxConsecutiveRecoveries})"
        ));

        var givesUp = ((rebuild is null) || (m_streak > MaxConsecutiveRecoveries));

        if (rebuild is null) {
            m_logger.LogError(
                exception: deviceLost,
                message: "Graphics device lost (reason 0x{Reason:X}), and this host has no way to rebuild it.",
                deviceLost.ReasonCode
            );
        } else if (givesUp) {
            m_logger.LogError(
                exception: deviceLost,
                message: "Graphics device-loss recovery failed {Count} times in a row (reason 0x{Reason:X}); ending the run.",
                MaxConsecutiveRecoveries,
                deviceLost.ReasonCode
            );
        } else {
            m_logger.LogWarning(
                exception: deviceLost,
                message: "Graphics device lost (reason 0x{Reason:X}); recovering.",
                deviceLost.ReasonCode
            );
        }

        // A still-working device (a reset, or the synthetic loss) must finish its work before anything on it is
        // destroyed; a removed one has nothing left to finish, and the drain says so by throwing, which is tolerated.
        if (m_rootHostContext.TryResolveCapability<IGpuDeviceContext>(capability: out var deviceContext)) {
            deviceContext.TryWaitIdle();
        }

        // Every object on the device is released before the device goes, and every capture armed at the loss is refused,
        // whether or not the run goes on: a run that gives up must not leave a capture to end as unserved or to meet a
        // disposed tree. The rebuild below replaces the device in place.
        m_root.OnDeviceLost();

        if ((rebuild is null) || givesUp) {
            return false;
        }

        var deadline = (m_time.GetTimestamp() + ((long)(ReacquireBudget.TotalSeconds * m_time.TimestampFrequency)));
        var waited = false;

        while (true) {
            try {
                rebuild.Rebuild();

                if (waited) {
                    m_logger.LogInformation(message: "A graphics device returned; its resources are rebuilt.");
                }

                return true;
            } catch (DeviceLostException absent) {
                if (m_time.GetTimestamp() >= deadline) {
                    m_logger.LogError(
                        exception: absent,
                        message: "The graphics device did not return within {Seconds}s of the loss (reason 0x{Reason:X}); it cannot be reinitialized in this process.",
                        ReacquireBudget.TotalSeconds,
                        absent.ReasonCode
                    );

                    return false;
                }

                if (!waited) {
                    m_logger.LogWarning(
                        message: "The graphics device is still absent; waiting up to {Seconds}s for it to return.",
                        ReacquireBudget.TotalSeconds
                    );
                    waited = true;
                }

                m_sleep(ReacquireBackoff);
            }
        }
    }
}
