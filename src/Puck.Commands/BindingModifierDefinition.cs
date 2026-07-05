namespace Puck.Commands;

/// <summary>
/// Declares an input source as a binding-page modifier: a control (for example, a gamepad trigger) whose held
/// state selects which <see cref="BindingPageDefinition"/> answers the other controls. Any source can be a
/// modifier — the profile data decides, not the engine — and an analog source is made digital here via
/// press/release thresholds with hysteresis, so a trigger resting near its threshold never flaps the page.
/// </summary>
/// <param name="Id">The profile-unique identifier a page chord references (e.g. <c>left</c>).</param>
/// <param name="Source">The provider-neutral input source id that drives the modifier (e.g. <c>gamepad.leftTrigger</c>).</param>
/// <param name="PressThreshold">The value at or above which the modifier latches held.</param>
/// <param name="ReleaseThreshold">The value at or below which a held modifier releases; at most <paramref name="PressThreshold"/>.</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
public sealed record BindingModifierDefinition(
    string Id,
    string Source,
    float PressThreshold = 0.5f,
    float ReleaseThreshold = 0.4f,
    string? Label = null,
    string? Icon = null
);
