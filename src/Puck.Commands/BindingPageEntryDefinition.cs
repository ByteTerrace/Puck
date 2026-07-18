using System.Text.Json.Serialization;

namespace Puck.Commands;

/// <summary>
/// One entry of a binding page: a physical source mapped to a command while that page is active. It carries the
/// full runtime expressiveness of a compiled <see cref="CommandBinding"/> — the incidental-modifier tolerance a
/// gameplay key needs (<paramref name="AnyModifiers"/>) and a constant activation value a digital source drives an
/// analog channel with (<paramref name="Value"/>) — plus the display metadata an on-screen binding UI presents
/// (both opaque strings the engine never interprets).
/// </summary>
/// <param name="Source">The provider-neutral input source id (an <c>InputSources</c> control, e.g. <c>gamepad.buttonSouth</c>).</param>
/// <param name="Command">The name of the command this source activates while the page is active.</param>
/// <param name="ActivateOn">The phase the binding fires on, or <see langword="null"/> for the default (press/continuous, not release).</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
/// <param name="AnyModifiers">Whether the binding fires REGARDLESS of the modifiers held — a gameplay key that must
/// keep working with an incidental Shift/Ctrl/Alt down (the movement-key convention). The default keeps the exact
/// (unmodified) chord match.</param>
/// <param name="Value">A constant activation value the digital source sends instead of its own (a function key driving
/// a fixed one-dimensional axis), or <see langword="null"/> to pass the source's value through. Marked
/// <see cref="JsonIgnoreAttribute"/>: it is a CODE-AUTHORING channel of the engine-default document (never present in a
/// stored/hand-edited profile or overlay), so a serialized binding stays a clean source→command mapping and neither
/// serializer needs the packed <see cref="CommandValue"/> vector.</param>
public sealed record BindingPageEntryDefinition(
    string Source,
    string Command,
    CommandPhase? ActivateOn = null,
    string? Label = null,
    string? Icon = null,
    bool AnyModifiers = false,
    [property: JsonIgnore] CommandValue? Value = null
);
