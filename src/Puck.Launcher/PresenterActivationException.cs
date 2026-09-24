using Puck.Abstractions.Presentation;

namespace Puck.Launcher;

/// <summary>
/// The failure <see cref="LauncherWindowHostedService"/> raises when <see cref="ISurfacePresenter.Activate"/> throws
/// against the window's surface, which is where a windowed host first brings its GPU device up. It faults the window
/// pump's hosted service and stops the host; the inner exception is the presenter's own failure, which a composition
/// root classifies (an absent adapter, driver, or loader is an environment the boot cannot run on).
/// </summary>
public sealed class PresenterActivationException : Exception {
    /// <summary>Initializes a new instance of the <see cref="PresenterActivationException"/> class.</summary>
    /// <param name="innerException">The exception <see cref="ISurfacePresenter.Activate"/> threw.</param>
    /// <exception cref="ArgumentNullException"><paramref name="innerException"/> is <see langword="null"/>.</exception>
    public PresenterActivationException(Exception innerException)
        : base(
        innerException: innerException,
        message: $"The surface presenter could not activate: {innerException?.Message}"
    ) => ArgumentNullException.ThrowIfNull(innerException);
}
