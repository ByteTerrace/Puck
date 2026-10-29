namespace Puck.World.Client;

/// <summary>A semantic action's current authority-published state, independent of its physical button.</summary>
/// <param name="Active">Whether the selected mode admits the action.</param>
/// <param name="AwaitingNeutral">Whether at least one source must return to neutral.</param>
/// <param name="Toggled">Whether an accepted destination-owned toggle is on.</param>
/// <param name="Retained">Whether the last boundary kept a nonzero level.</param>
public readonly record struct WorldInputActionStatus(bool Active, bool AwaitingNeutral, bool Toggled, bool Retained);
