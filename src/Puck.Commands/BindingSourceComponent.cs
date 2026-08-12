namespace Puck.Commands;

/// <summary>The two components a two-dimensional physical control (a stick, a touchpad) can be decomposed into.</summary>
public enum AxisComponent : byte {
    /// <summary>The control's X component.</summary>
    X,

    /// <summary>The control's Y component.</summary>
    Y,
}

/// <summary>
/// Parses a <see cref="BindingPageEntryDefinition.Source"/>'s optional axis-component suffix — <c>leftStick.x</c>
/// naming the X component of the two-dimensional <c>gamepad.leftStick</c> control, feeding a channel destination
/// with that component's analog magnitude instead of the constant a bare Axis2D source falls back to (see
/// <see cref="InputRouter"/>'s <c>ResolveValue</c>). The suffix is a pure string convention over the existing
/// physical-source vocabulary — <c>Puck.Commands</c> carries no knowledge of which source ids the engine actually
/// declares (that lives in <c>Puck.Input.InputSources</c>, an assembly this project cannot reference); the
/// vocabulary half — does the base id name a real, two-dimensional control — is
/// <see cref="BindingVocabularyCheck"/>'s job, fed the same split this type computes.
/// </summary>
/// <remarks>
/// A component reference is exactly a third dot-separated segment whose value is <c>x</c> or <c>y</c> (lowercase,
/// ordinal) — <c>gamepad.leftStick.x</c> splits to base <c>gamepad.leftStick</c> and <see cref="AxisComponent.X"/>.
/// A source with only one dot (<c>gamepad.leftStick</c>, <c>keyboard.f10</c>) has no third segment and is an
/// ordinary source, not a component reference. A source with two or more dots whose final segment is anything
/// other than <c>x</c>/<c>y</c> is malformed — refused by <see cref="BindingProfile.Compile"/> by name, the same
/// way an unrecognized activator or chord shape is, rather than silently resolving as an unknown plain source.
/// </remarks>
public static class BindingSourceComponent {
    /// <summary>Attempts to split a source id into its base control and axis component.</summary>
    /// <param name="source">The authored source id, e.g. <c>gamepad.leftStick.x</c>.</param>
    /// <param name="baseSource">The base physical-control id a raw <see cref="InputSignal.Source"/> actually
    /// carries (e.g. <c>gamepad.leftStick</c>) — <paramref name="source"/> unchanged when it names no component.</param>
    /// <param name="component">The named component, or <see langword="null"/> when <paramref name="source"/> names
    /// no component (an ordinary source with at most one dot).</param>
    /// <returns><see langword="false"/> when <paramref name="source"/> has two or more dots but its final segment
    /// is not exactly <c>x</c> or <c>y</c> — a malformed component reference. <see langword="true"/> otherwise,
    /// whether or not a component was found.</returns>
    public static bool TrySplit(string source, out string baseSource, out AxisComponent? component) {
        baseSource = source;
        component = null;

        var lastDot = source.LastIndexOf(value: '.');

        if (lastDot <= 0) {
            // No dot, or a leading dot with nothing before it — not a well-formed source at all; the structural
            // gate elsewhere refuses an empty/missing source, this is not that check's job.
            return true;
        }

        var firstDot = source.IndexOf(value: '.');

        if (firstDot == lastDot) {
            // Exactly one dot (gamepad.leftStick, keyboard.f10) — an ordinary source, no component suffix.
            return true;
        }

        var suffix = source[(lastDot + 1)..];

        if (string.Equals(
            a: suffix,
            b: "x",
            comparisonType: StringComparison.Ordinal
        )) {
            component = AxisComponent.X;
            baseSource = source[..lastDot];

            return true;
        }

        if (string.Equals(
            a: suffix,
            b: "y",
            comparisonType: StringComparison.Ordinal
        )) {
            component = AxisComponent.Y;
            baseSource = source[..lastDot];

            return true;
        }

        return false;
    }
}
