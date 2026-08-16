using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// Declares the record value shape a physical <see cref="InputSources"/> control's activation carries, in the
/// engine's shared <see cref="CommandValueKind"/> vocabulary. <see cref="InputSources"/> is the single home for
/// physical-control names; this is the single home for what shape each one's value takes, so a consumer reads
/// the classification by reflection instead of re-transcribing its own per-id copy that can fall behind.
/// </summary>
/// <remarks>
/// <see cref="InputSourceVocabulary.TryResolveDeclaredKind"/> reads this attribute and reports the FULL declared
/// range unnarrowed; a downstream caller that can only carry a subset (<see cref="CommandValueKind"/> also covers
/// three-/four-component motion) — the <see cref="CommandValueKind.Digital"/>/<see cref="CommandValueKind.Axis1D"/>/
/// <see cref="CommandValueKind.Axis2D"/> subset an addon input act's two payload value lanes can hold, for example
/// (see <c>Puck.Scripting.AddonSourceShape</c>) — applies that narrowing itself. A control whose declared
/// <see cref="Kind"/> falls outside such a subset is a real, recognized control that caller cannot carry, not an
/// unrecognized one.
/// </remarks>
[AttributeUsage(validOn: AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class InputSourceValueAttribute : Attribute {
    /// <summary>Declares the source's value <paramref name="kind"/>.</summary>
    /// <param name="kind">The value shape the control's activation carries.</param>
    public InputSourceValueAttribute(CommandValueKind kind) {
        Kind = kind;
    }

    /// <summary>The value shape the control's activation carries.</summary>
    public CommandValueKind Kind { get; }
}
