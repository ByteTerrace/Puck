namespace Puck.Input;

/// <summary>
/// An OPTIONAL filter the window pump offers every raw window input event before anything else sees it, resolved as a
/// HELD root <c>IHostContext</c> capability the way <see cref="IWindowInputObserver"/> is. An event the filter consumes
/// reaches neither the observer nor <see cref="WindowInputMapper"/> and the command router, so the game never sees it;
/// an event it declines continues exactly as if no filter were registered. With none registered, the pump skips the
/// call.
/// </summary>
public interface IWindowInputFilter {
    /// <summary>Offers one raw window input event, in the window's order, on the window-pump thread.</summary>
    /// <param name="inputEvent">The dequeued event.</param>
    /// <returns><see langword="true"/> when the filter consumed the event.</returns>
    bool Intercept(in WindowInputEvent inputEvent);
}
