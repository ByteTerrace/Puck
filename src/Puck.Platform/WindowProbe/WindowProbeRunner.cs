using Microsoft.Extensions.Options;

namespace Puck.Platform.WindowProbe;

public sealed class WindowProbeRunner(IOptions<WindowProbeOptions> options, INativeWindow nativeWindow) {
    private const int NotPaintedExitCode = 2;
    private const int NotVisibleExitCode = 1;
    private const int SuccessExitCode = 0;
    private const int TimedOutExitCode = 3;

    private readonly INativeWindow m_nativeWindow = nativeWindow;
    private readonly WindowProbeOptions m_options = options.Value;

    public int Run() {
        m_nativeWindow.Show();

        var remainingPolls = m_options.MaxPumpIterations;

        while (
            m_nativeWindow.IsOpen &&
            ((remainingPolls > 0) || (m_options.MaxPumpIterations == 0))
        ) {
            m_nativeWindow.PollEvents();

            if (
                m_options.AutoCloseAfterFirstPaint &&
                m_nativeWindow.HasPainted
            ) {
                m_nativeWindow.Close();

                if (m_nativeWindow.IsOpen) {
                    m_nativeWindow.PollEvents();
                }
            }

            if (!m_nativeWindow.IsOpen) {
                return SuccessExitCode;
            }

            if (m_options.MaxPumpIterations > 0) {
                remainingPolls--;
            }

            if (m_options.PollDelayMilliseconds > 0) {
                Thread.Sleep(millisecondsTimeout: m_options.PollDelayMilliseconds);
            }
        }

        if (!m_nativeWindow.IsVisible) {
            return NotVisibleExitCode;
        }

        if (!m_nativeWindow.HasPainted) {
            return NotPaintedExitCode;
        }

        return TimedOutExitCode;
    }
}
