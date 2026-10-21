using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Presentation;
using Puck.Commands;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    // The screens a seat's view frames this frame, each with the rect its glass fills.
    private readonly List<(int Screen, NormalizedRect Region)> m_framing = [];
    // The pane mapping last published for each framed screen, kept while its screen mapping, rect and destination hold,
    // so a steady frame publishes without allocating.
    private readonly Dictionary<int, (SourceMapping Glass, SourceMapping Pane)> m_framedMappings = [];
    // The source instances the panes last published show as framed screens, and each framed screen's published pane.
    private readonly HashSet<string> m_framedShown = new(comparer: StringComparer.Ordinal);
    private readonly List<(int Screen, SourceMapping Pane)> m_framedPanes = [];

    /// <summary>Records that a seat's view frames a screen this frame: its camera frames the screen's picture, the glass
    /// inside its bezel band, square-on so it fills exactly a rect of the display, the seat's matte
    /// (<see cref="WorldScreenFraming.Matte"/>). <see cref="PublishPanes"/> then publishes the screen's own mapping
    /// (<see cref="Screens"/>) placed on that rect as a pane, which the picker and the passthrough router read: square-on,
    /// a display point maps onto the source exactly as the ray through it meets the picture. A screen already framed this
    /// frame, or one that publishes no mapping, publishes nothing more.</summary>
    /// <param name="screen">The framed screen's index.</param>
    /// <param name="region">The rect of the display the picture fills, in normalized display coordinates.</param>
    public void FrameScreen(int screen, NormalizedRect region) {
        foreach (var (framed, _) in m_framing) {
            if (framed == screen) {
                return;
            }
        }

        m_framing.Add(item: (screen, region));
    }
    /// <summary>Returns whether the panes last published show an instance as the source of a framed screen
    /// (<see cref="FrameScreen"/>).</summary>
    /// <param name="instance">The source instance's name.</param>
    /// <returns><see langword="true"/> when a published pane is a framed screen showing the instance.</returns>
    public bool ShowsFramed(string instance) => m_framedShown.Contains(item: instance);
    /// <summary>Finds the pane the panes last published for a framed screen (<see cref="FrameScreen"/>): the source
    /// instance its picture shows and the rect of the display the picture fills.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <param name="instance">The source instance the picture shows, when this returns <see langword="true"/>.</param>
    /// <param name="region">The rect the picture fills, in normalized display coordinates, when this returns
    /// <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a published pane frames <paramref name="screen"/>.</returns>
    public bool TryFramedScreen(int screen, [NotNullWhen(returnValue: true)] out string? instance, out NormalizedRect region) {
        foreach (var (framed, pane) in m_framedPanes) {
            if (
                (framed == screen) &&
                (pane.Placement is SourcePlacement.Pane { Region: var shown })
            ) {
                instance = pane.Source.Name;
                region = shown;

                return true;
            }
        }

        instance = null;
        region = default;

        return false;
    }

    // Publishes each framed screen's mapping on the rect its glass fills, after every pane the root draws, so it is
    // topmost: the presentation destination, or the passthrough destination once the local user opened its source.
    private void PublishFramed() {
        m_framedShown.Clear();
        m_framedPanes.Clear();

        foreach (var (screen, region) in m_framing) {
            if (
                (Screens is not { } screens) ||
                !screens.TryGet(
                    mapping: out var glass,
                    screen: screen
                )
            ) {
                continue;
            }

            var passthrough = m_passthrough.Contains(item: glass.Source.Name);
            var destination = (passthrough
                ? SourceDestination.Passthrough
                : SourceDestination.Presentation);

            if (
                !m_framedMappings.TryGetValue(
                    key: screen,
                    value: out var cached
                ) ||
                !ReferenceEquals(
                    objA: cached.Glass,
                    objB: glass
                ) ||
                (cached.Pane.Placement is not SourcePlacement.Pane { Region: var shown }) ||
                (shown != region) ||
                (cached.Pane.Destination != destination)
            ) {
                // The rect is the picture inside the glass's bezel band, so the pane maps it straight onto the image.
                var mapping = (glass with {
                    Destination = destination,
                    Opener = (passthrough
                        ? SourceOpener.LocalUser
                        : SourceOpener.Document),
                    Placement = new SourcePlacement.Pane(Region: region),
                    Warp = null,
                });

                if (!mapping.TryValidate(refusal: out _)) {
                    _ = m_framedMappings.Remove(key: screen);

                    continue;
                }

                cached = (glass, mapping);
                m_framedMappings[screen] = cached;
            }

            m_panes.Add(item: cached.Pane);
            m_framedPanes.Add(item: (screen, cached.Pane));
            _ = m_framedShown.Add(item: cached.Pane.Source.Name);
        }
    }
}
