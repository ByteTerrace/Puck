using Puck.Commands;
using Puck.Input;
using Puck.Scripting;

namespace Puck.World;

/// <summary>
/// Narrows <see cref="InputSourceVocabulary"/>'s full <see cref="CommandValueKind"/> range down to the
/// <see cref="AddonSourceShape"/> subset (<see cref="CommandValueKind.Digital"/>/<see cref="CommandValueKind.Axis1D"/>/
/// <see cref="CommandValueKind.Axis2D"/>) an addon input act's two-lane payload record can hold. Native input
/// bindings do not use this narrowing: they carry the full <see cref="CommandValue"/> range directly.
/// </summary>
internal static class AddonSourceVocabulary {
    /// <summary>Attempts to resolve <paramref name="sourceId"/> to the record value shape it carries.</summary>
    /// <param name="sourceId">The provider-neutral source id text (e.g. <c>"gamepad.buttonSouth"</c>).</param>
    /// <param name="shape">When this returns <see langword="true"/>, the record value shape.</param>
    /// <returns><see langword="true"/> if <paramref name="sourceId"/> names a source this shape can carry; otherwise
    /// <see langword="false"/> — either the id is unrecognized, or it names a real control
    /// (<see cref="IsUnaddressable"/>) this shape cannot carry.</returns>
    public static bool TryResolve(string sourceId, out AddonSourceShape shape) {
        shape = default;

        if (InputSourceVocabulary.IsExplicitlyUnaddressable(sourceId: sourceId)) {
            return false;
        }

        if (!InputSourceVocabulary.TryResolveDeclaredKind(kind: out var kind, sourceId: sourceId)) {
            return false;
        }

        switch (kind) {
            case CommandValueKind.Digital:
                shape = AddonSourceShape.Digital;
                return true;
            case CommandValueKind.Axis1D:
                shape = AddonSourceShape.Axis1D;
                return true;
            case CommandValueKind.Axis2D:
                shape = AddonSourceShape.Axis2D;
                return true;
            default:
                return false;
        }
    }
    /// <summary>Indicates whether <paramref name="sourceId"/> names a control the engine genuinely has but this
    /// shape cannot carry — a text-bearing key, or a motion source with more components than the shape's two-lane
    /// record holds. <see cref="TryResolve"/> refuses these alongside unknown ids; this separates them so a
    /// refusal can say which mistake was made.</summary>
    /// <param name="sourceId">The provider-neutral source id text.</param>
    /// <returns><see langword="true"/> when the id names a real control this shape cannot express.</returns>
    public static bool IsUnaddressable(string sourceId) {
        if (InputSourceVocabulary.IsExplicitlyUnaddressable(sourceId: sourceId)) {
            return true;
        }

        return (InputSourceVocabulary.TryResolveDeclaredKind(kind: out var kind, sourceId: sourceId)
            && (kind is not (CommandValueKind.Digital or CommandValueKind.Axis1D or CommandValueKind.Axis2D)));
    }
}
