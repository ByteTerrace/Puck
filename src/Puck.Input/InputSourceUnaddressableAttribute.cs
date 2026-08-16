namespace Puck.Input;

/// <summary>
/// Marks a physical <see cref="InputSources"/> control that a fixed-shape downstream record cannot carry for a
/// reason beyond its declared <see cref="InputSourceValueAttribute.Kind"/> — today only a text payload
/// (<see cref="InputSources.Keyboard.Text"/>), whose <see cref="Puck.Commands.CommandValueKind.Digital"/> kind is
/// perfectly representable and says nothing about the text riding beside it.
/// <see cref="InputSourceVocabulary.IsExplicitlyUnaddressable"/> reports this marker alone; a control whose
/// <see cref="InputSourceValueAttribute.Kind"/> already falls outside a caller's own addressable subset (the
/// three-/four-component motion sources, for a caller that can only carry
/// <see cref="Puck.Commands.CommandValueKind.Digital"/>/<see cref="Puck.Commands.CommandValueKind.Axis1D"/>/
/// <see cref="Puck.Commands.CommandValueKind.Axis2D"/>; see <c>Puck.Scripting.AddonSourceShape</c>) needs no
/// separate marker — that caller derives it from the kind alone; see <see cref="InputSourceValueAttribute"/>'s
/// remarks.
/// </summary>
[AttributeUsage(validOn: AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class InputSourceUnaddressableAttribute : Attribute {
}
