namespace Puck.Input.Devices;

/// <summary>Resets parser state that belongs to one physical controller stream when a receiver slot is reused.</summary>
internal interface IGamepadStreamReset {
    void ResetStreamState();
}
