using Puck.Abstractions.Presentation;

namespace Puck.Launcher;

// The inherited capability borrows its presenter. Returning the presenter itself from a second singleton factory
// would make the DI container own and dispose the same device-facing object twice. Resolution remains lazy so
// resolving a host context cannot construct a presenter; offscreen hosts provide no resolver and stay unavailable.
internal sealed class LauncherPresentTiming(Func<IPresentTimingFeedback?>? resolve = null) : IPresentTimingFeedback {
    private readonly Lazy<IPresentTimingFeedback?>? m_source = ((resolve is null) ? null : new(valueFactory: resolve));

    public PresentTimingSample LastPresentTiming => (m_source?.Value?.LastPresentTiming ?? PresentTimingSample.Unavailable);
}
