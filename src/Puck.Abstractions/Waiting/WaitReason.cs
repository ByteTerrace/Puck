namespace Puck.Abstractions.Waiting;

/// <summary>Why a thread waits: the subsystem that waits and what it waits for. Both are constant strings, so a reason
/// costs nothing to pass and reads the same in <c>world.wait</c>, <c>host.frames</c> and a narrated expiry.</summary>
/// <param name="Subsystem">The waiting subsystem, such as <c>"machines"</c>.</param>
/// <param name="WaitsFor">What it waits for, such as <c>"the worker's step"</c>.</param>
public readonly record struct WaitReason(string Subsystem, string WaitsFor) {
    /// <summary>Returns the reason as <c>subsystem: waits-for</c>.</summary>
    /// <returns>The reason's text.</returns>
    public override string ToString() => $"{Subsystem}: {WaitsFor}";
}
