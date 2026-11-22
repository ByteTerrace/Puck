namespace Puck.Abstractions.Pacing;

/// <summary>The simulation's window for synchronous work on the host pump.</summary>
public interface IHostTickBoundary {
    /// <summary>Registers the pump before its first input capture.</summary>
    void RegisterPump();
    /// <summary>Unregisters the pump after its last iteration.</summary>
    void UnregisterPump();
    /// <summary>Opens the window on the pump.</summary>
    /// <param name="drain">Whether deferred submissions can drain at a settled boundary.</param>
    void Open(bool drain);
    /// <summary>Closes the current window.</summary>
    void Close();
}
