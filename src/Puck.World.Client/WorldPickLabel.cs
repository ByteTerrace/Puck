using System.Globalization;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>Names what a GPU pick answered for a hover label. A label carries the pixel its answer was sampled at, so a
/// label shown while the pointer moves on names a placement only at the coordinates that placement was answered for.
/// The text is rebuilt only when the target or the pixel changes, so a steady hover allocates nothing.</summary>
public sealed class WorldPickLabel {
    private WorldPickTarget? m_target;
    private uint m_x;
    private uint m_y;
    private string? m_text;

    /// <summary>Returns the label of a pick.</summary>
    /// <param name="pick">The published answer, or null before one.</param>
    /// <returns>The placement or body and the pixel it was answered at, or null when the answer names neither.</returns>
    public string? Of(SdfPickResult? pick) {
        var target = (pick?.Target as WorldPickTarget);

        if ((target is null) || (pick is not { } answer)) {
            m_target = null;
            m_text = null;
            return null;
        }
        if (!ReferenceEquals(objA: m_target, objB: target) || (m_x != answer.X) || (m_y != answer.Y)) {
            m_target = target;
            m_x = answer.X;
            m_y = answer.Y;
            m_text = target switch {
                { Placement: { } placement } => string.Create(provider: CultureInfo.InvariantCulture, handler: $"placement '{placement}' at {answer.X},{answer.Y}"),
                { BodyIndex: { } body } => string.Create(provider: CultureInfo.InvariantCulture, handler: $"body {body} at {answer.X},{answer.Y}"),
                _ => null,
            };
        }
        return m_text;
    }
}
