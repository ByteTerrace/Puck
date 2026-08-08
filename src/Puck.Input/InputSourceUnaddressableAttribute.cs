namespace Puck.Input;

/// <summary>
/// Marks a physical <see cref="InputSources"/> control that no fixed-shape addon record can carry for a reason
/// beyond its declared <see cref="InputSourceValueAttribute.Kind"/> — today only a text payload
/// (<see cref="InputSources.Keyboard.Text"/>), whose <see cref="Puck.Commands.CommandValueKind.Digital"/> kind is
/// perfectly representable and says nothing about the text riding beside it. A control whose <see cref="InputSourceValueAttribute.Kind"/>
/// already falls outside the shapes an addon input record carries (the three-/four-component motion sources; see
/// <c>Puck.Scripting.AddonSourceShape</c>) needs no separate marker — its kind alone says so; see
/// <see cref="InputSourceValueAttribute"/>'s remarks.
/// </summary>
[AttributeUsage(validOn: AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class InputSourceUnaddressableAttribute : Attribute {
}
