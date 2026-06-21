namespace Puck.Input.Devices;

/// <summary>
/// An acquisition backend that discovers controllers on its own schedule and publishes them into the
/// <c>GamepadManager</c> through a <see cref="IGamepadConnectionRegistry"/>. The HID path is built into the
/// manager; this seam exists for transports the manager cannot run itself — notably the Windows-only XInput +
/// GameInput Xbox backend in <c>Puck.Platform</c>, which owns its own poll thread. The manager calls
/// <see cref="Start"/> once and disposes the source on shutdown.
/// </summary>
public interface IGamepadAcquisitionSource : IDisposable
{
    /// <summary>
    /// Starts acquisition. The source begins discovering devices and registering them with
    /// <paramref name="registry"/>; it owns any background thread it needs until disposed.
    /// </summary>
    /// <param name="registry">The registry the source publishes its connections into.</param>
    void Start(IGamepadConnectionRegistry registry);
}
