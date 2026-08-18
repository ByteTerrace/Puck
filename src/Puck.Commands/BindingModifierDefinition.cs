using System.Text.Json.Serialization;

namespace Puck.Commands;

/// <summary>
/// Declares an input source as a binding-page modifier: a control (for example, a gamepad trigger) whose held
/// state selects which <see cref="BindingPageDefinition"/> answers the other controls. Any source can be a
/// modifier — the profile data decides, not the engine — and an analog source is made digital here via
/// press/release thresholds with hysteresis, so a trigger resting near its threshold never flaps the page. A
/// modifier may name several sources (e.g. a gamepad shoulder AND a keyboard key both opening the same wheel): the
/// modifier is HELD while ANY of them is down, tracked per source so releasing one while another is still down
/// leaves the modifier held; the modifier's press order (for a chord) is the FIRST of its sources to press.
/// </summary>
/// <param name="Id">The profile-unique identifier a page chord references (e.g. <c>left</c>).</param>
/// <param name="Sources">The provider-neutral input source ids that drive the modifier (e.g.
/// <c>gamepad.leftTrigger</c>), at least one; <see cref="BindingProfile.Compile"/> refuses a source repeated within
/// one modifier's list and a source two different modifiers both claim.</param>
/// <param name="PressThreshold">The value at or above which a source latches down.</param>
/// <param name="ReleaseThreshold">The value at or below which a down source releases; at most <paramref name="PressThreshold"/>.</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingModifierDefinition(
    string Id,
    IReadOnlyList<string> Sources,
    float PressThreshold = 0.5f,
    float ReleaseThreshold = 0.4f,
    string? Label = null,
    string? Icon = null
);
