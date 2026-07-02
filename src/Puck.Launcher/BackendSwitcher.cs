using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
namespace Puck.Launcher;

/// <summary>
/// An <see cref="ISurfacePresenter"/> that fronts up to two backends and forwards every call to the active one,
/// swapping which is active on <see cref="Switch"/>. The host loop presents through this seam, so the live
/// backend can change without the loop knowing. When only one backend is available, <see cref="Switch"/> is a
/// no-op. It is backend-neutral (it knows only <see cref="ISurfacePresenter"/> + a display name), so it lives in the
/// generic launcher; the composition root supplies the concrete presenters via <see cref="SurfacePresenterDescriptor"/>.
/// </summary>
public sealed class BackendSwitcher : ISurfacePresenter, IPresentTimingFeedback, IDeviceLostRecoverable {
    private ISurfacePresenter m_current;
    private string m_currentName;
    private ISurfacePresenter? m_other;
    private string? m_otherName;
    private NativeSurfaceBinding m_binding;
    private uint m_height;
    private bool m_initialized;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="BackendSwitcher"/> class.</summary>
    /// <param name="current">The initially active presenter.</param>
    /// <param name="currentName">The active presenter's display name.</param>
    /// <param name="other">The alternative presenter, or <see langword="null"/> when only one backend exists.</param>
    /// <param name="otherName">The alternative presenter's display name, or <see langword="null"/> when <paramref name="other"/> is.</param>
    /// <exception cref="ArgumentException"><paramref name="other"/> and <paramref name="otherName"/> are not both null or both non-null.</exception>
    public BackendSwitcher(ISurfacePresenter current, string currentName, ISurfacePresenter? other, string? otherName) {
        if ((other is null) != (otherName is null)) {
            throw new ArgumentException(message: "The 'other' presenter and 'otherName' must both be null or both be non-null.");
        }

        m_current = current;
        m_currentName = currentName;
        m_other = other;
        m_otherName = otherName;
    }

    /// <summary>Gets the active backend's display name.</summary>
    public string ActiveBackendName => m_currentName;

    /// <inheritdoc/>
    /// <remarks>Forwards to the active backend when it reports present timing (closed-loop pacing); a backend that does
    /// not implement <see cref="IPresentTimingFeedback"/> yields <see cref="PresentTimingSample.Unavailable"/>, so the
    /// pacer stays open-loop until/unless a switch makes a timing-capable backend active.</remarks>
    public PresentTimingSample LastPresentTiming =>
        ((m_current is IPresentTimingFeedback feedback)
            ? feedback.LastPresentTiming
            : PresentTimingSample.Unavailable);

    /// <inheritdoc/>
    /// <remarks>Forwards to the active backend when it can recover; throws otherwise so the host treats the loss as
    /// unrecoverable (a backend without the capability cannot rebuild its device).</remarks>
    public void RecoverFromDeviceLoss(NativeSurfaceBinding binding, uint width, uint height) {
        if (m_current is not IDeviceLostRecoverable recoverable) {
            throw new NotSupportedException(message: $"The active backend '{m_currentName}' cannot recover from device loss.");
        }

        recoverable.RecoverFromDeviceLoss(binding: binding, height: height, width: width);
    }

    /// <inheritdoc/>
    public void Activate(NativeSurfaceBinding binding, uint width, uint height) {
        m_binding = binding;
        m_width = width;
        m_height = height;
        m_initialized = true;
        m_current.Activate(binding: binding, width: width, height: height);
    }
    /// <inheritdoc/>
    public void Deactivate() {
        if (m_initialized) {
            m_current.Deactivate();
        }
    }
    /// <inheritdoc/>
    public void BeginFrame(uint width, uint height) {
        m_width = width;
        m_height = height;
        m_current.BeginFrame(width: width, height: height);
    }
    /// <inheritdoc/>
    public void Present(Surface surface) {
        m_current.Present(surface: surface);
    }
    /// <summary>Swaps the active backend, deactivating the current one and activating the other; a no-op when no
    /// alternative is available or the presenter has not been activated. On activation failure the previous
    /// backend is restored and the exception rethrown.</summary>
    public void Switch() {
        if (!m_initialized || m_other is null) {
            return;
        }

        m_current.Deactivate();
        (m_current, m_currentName, m_other, m_otherName) = (m_other, m_otherName!, m_current, m_currentName);

        try {
            m_current.Activate(binding: m_binding, width: m_width, height: m_height);
        } catch {
            (m_current, m_currentName, m_other, m_otherName) = (m_other, m_otherName!, m_current, m_currentName);
            m_current.Activate(binding: m_binding, width: m_width, height: m_height);
            throw;
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        Deactivate();
    }
}
