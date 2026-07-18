using System.CommandLine;

namespace Puck.Commands;

/// <summary>
/// Carries the per-invocation state passed to a command handler, unifying the text-parsing and
/// source-driven activation paths behind a single signature.
/// </summary>
/// <param name="Value">The command value for this invocation.</param>
/// <param name="Phase">The transition this invocation represents.</param>
/// <param name="Parse">The parse result when the command was invoked from text; otherwise <see langword="null"/>.</param>
/// <param name="Text">An optional text payload supplied by the activation.</param>
/// <param name="Registry">
/// The registry that dispatched the invocation, allowing handlers to query or affect command state.
/// May be <see langword="null"/> when no registry context is available.
/// </param>
/// <param name="DeviceId">
/// The device that produced the activation (for source-driven input), letting a handler target the specific
/// device — e.g. rumbling the controller that pressed the button. <see langword="default"/> for the text path.
/// </param>
/// <param name="Slot">
/// The stable logical player lane that owns the invocation. Snapshot-driven handlers must use this, rather than
/// <paramref name="DeviceId"/>, when choosing simulation state: device identities are local annotations and are not
/// serialized into recordings. The immediate/text path defaults to slot <c>0</c>.
/// </param>
/// <param name="AssignedSlot">Whether this invocation's physical signal created its device-to-slot assignment.
/// Snapshot-driven handlers use this deterministic bit to distinguish a first-seat gesture from an ordinary action;
/// it remains valid during replay even though <paramref name="DeviceId"/> is local-only.</param>
public readonly record struct CommandContext(
    CommandValue Value,
    CommandPhase Phase,
    ParseResult? Parse,
    string? Text = null,
    CommandRegistry? Registry = null,
    InputDeviceId DeviceId = default,
    int Slot = 0,
    bool AssignedSlot = false
);
