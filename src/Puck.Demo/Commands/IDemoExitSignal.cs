namespace Puck.Demo.Commands;

/// <summary>One-shot exit request raised by the <c>exit</c> command or the menu's Exit entry, drained
/// by the pump loop to close the window (mirrors the engine's ITerminalExitSignal / MainMenuState).</summary>
internal interface IDemoExitSignal {
    void RequestExit();
    bool TryConsumeExit();
}
