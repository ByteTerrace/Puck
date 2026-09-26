using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Commands;
using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The windowed host's passthrough door: the panes the local user opened as passthrough sources, the window each one's
/// capture shows, and the <see cref="SourcePassthroughRouter"/> the window pump offers every raw event to
/// (<see cref="IWindowInputFilter"/>). Only <see cref="WorldPassthroughCommandModule"/>'s <c>source.passthrough open</c>,
/// which the host's own console alone may run, adds a source; nothing a world document declares reaches it.
/// </summary>
/// <remarks>
/// <para>A source stays open while a published pane shows it and its capture keeps showing the window it showed when it
/// was opened. Before each event is routed, a source whose pane is no longer published, whose instance stopped, whose
/// capture reopened onto another window, or whose row a row of the same name replaced is closed, so a window the local
/// user did not open, or can no longer see, never receives input. Closing a source releases every key and button its
/// window holds and returns the keyboard to the game when the source had it.</para>
/// <para>Window-pump-thread only: the console's immediate verbs and the pump's events run there.</para>
/// </remarks>
internal sealed class WorldSourcePassthrough : IWindowInputFilter, ISourcePassthroughWindows {
    private readonly WorldScreenBinder m_binder;
    private readonly WorldViewGraphHost m_graphs;
    private readonly SortedDictionary<string, Opened> m_opened = new(comparer: StringComparer.Ordinal);
    private readonly WorldSeatViewports m_viewports;

    // An opened source: the handle its pane's mapping names it by, and the window the local user opened it on.
    private readonly record struct Opened(SourceHandle Source, ISourcePassthroughWindow Window);

    /// <summary>Initializes a new instance of the <see cref="WorldSourcePassthrough"/> class.</summary>
    /// <param name="binder">The binder whose source instances' capture feeds name their windows.</param>
    /// <param name="graphs">The render graph host whose published panes the router reads.</param>
    /// <param name="viewports">The viewports whose client extent scales a pointer position into display pixels.</param>
    public WorldSourcePassthrough(WorldScreenBinder binder, WorldViewGraphHost graphs, WorldSeatViewports viewports) {
        m_binder = binder;
        m_graphs = graphs;
        m_viewports = viewports;
        Router = new SourcePassthroughRouter(windows: this);
    }

    /// <summary>Gets the router that hosts the keyboard focus.</summary>
    public SourcePassthroughRouter Router { get; }

    // The first opened source that must close: no published pane shows it, or its capture no longer shows the window
    // it was opened on.
    private string? Stale() {
        foreach (var (instance, opened) in m_opened) {
            if (
                !m_graphs.TryGetPane(
                    instance: instance,
                    mapping: out _
                ) ||
                !ReferenceEquals(
                    objA: m_binder.PassthroughWindowOf(
                        fault: out _,
                        instance: instance
                    ),
                    objB: opened.Window
                )
            ) {
                return instance;
            }
        }

        return null;
    }

    /// <summary>Opens a pane's capture as a passthrough source the local user opened.</summary>
    /// <param name="instance">The instance a shown pane draws.</param>
    /// <param name="windowTitle">The title the local user names the window by; the window the pane captures must carry
    /// it, compared without regard to case.</param>
    /// <param name="message">What was opened, or why nothing was.</param>
    /// <returns><see langword="true"/> when the source is open.</returns>
    public bool TryOpen(string instance, string windowTitle, out string message) {
        if (!m_graphs.TryGetPane(
            instance: instance,
            mapping: out var mapping
        )) {
            message = $"no shown pane draws '{instance}' (world.view.panes lists them)";

            return false;
        }
        if (m_binder.PassthroughWindowOf(
            fault: out var fault,
            instance: instance
        ) is not { } window) {
            message = fault!;

            return false;
        }

        var title = window.Title;

        if (!title.Contains(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: windowTitle
        )) {
            message = $"'{instance}' captures the window '{title}', whose title does not contain '{windowTitle}'";

            return false;
        }

        _ = Close(instance: instance);
        m_opened[instance] = new Opened(
            Source: mapping.Source,
            Window: window
        );
        m_graphs.OpenPassthrough(instance: instance);
        message = $"'{instance}' opened as a passthrough source for the window '{title}'; a click on its pane gives it the keyboard, and Control+Alt+Escape returns it to the game";

        return true;
    }
    /// <summary>Closes a passthrough source: releases every key and button its window holds, to that window, returns the
    /// keyboard to the game when the source had it, and returns its pane to the presentation destination.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns><see langword="true"/> when the source was open.</returns>
    public bool Close(string instance) {
        if (!m_opened.TryGetValue(
            key: instance,
            value: out var opened
        )) {
            return false;
        }

        // Revoked while the window still resolves, so the releases reach it.
        Router.Revoke(source: opened.Source);
        _ = m_opened.Remove(key: instance);
        _ = m_graphs.ClosePassthrough(instance: instance);

        return true;
    }
    /// <summary>Describes the opened sources and where the keyboard is, the <c>source.passthrough</c> read-back.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        var builder = new StringBuilder(value: "keyboard ");

        _ = builder.Append(value: ((Router.Focus.Focused is { } focused)
            ? focused.Name
            : "game"
        ));

        if (m_opened.Count == 0) {
            return builder.Append(value: "; none opened").ToString();
        }

        foreach (var (instance, opened) in m_opened) {
            var window = opened.Window;

            _ = builder.Append(value: "; ").Append(value: instance).Append(value: " window '").Append(value: window.Title).Append(value: '\'');
            _ = (window.TryDescribe(window: out var described)
                ? builder.Append(provider: CultureInfo.InvariantCulture, handler: $" frame {described.FrameWidth}x{described.FrameHeight} client {described.Client.Width}x{described.Client.Height} at {described.Client.X},{described.Client.Y} scale {described.DpiScale:0.###}")
                : builder.Append(value: " gone"));
        }

        return builder.ToString();
    }
    /// <inheritdoc/>
    public bool Intercept(in WindowInputEvent inputEvent) {
        Router.Publish(
            displayHeight: m_graphs.DisplayHeight,
            displayWidth: m_graphs.DisplayWidth,
            panes: m_graphs.Panes
        );

        while (Stale() is { } stale) {
            _ = Close(instance: stale);
        }

        if (
            (inputEvent.Kind == WindowInputKind.PointerPosition) &&
            (m_viewports.ClientWidth > 0) &&
            (m_viewports.ClientHeight > 0)
        ) {
            // The pointer arrives in client pixels; the panes are published in display pixels, the stretch the drawn
            // cursor's pane hover applies too.
            return Router.Route(inputEvent: inputEvent with {
                Vector = new Vector2(
                    x: (inputEvent.Vector.X * (m_graphs.DisplayWidth / ((float)m_viewports.ClientWidth))),
                    y: (inputEvent.Vector.Y * (m_graphs.DisplayHeight / ((float)m_viewports.ClientHeight)))
                ),
            });
        }

        return Router.Route(inputEvent: in inputEvent);
    }
    /// <inheritdoc/>
    public bool TryGet(SourceHandle source, [NotNullWhen(returnValue: true)] out ISourcePassthroughWindow? window) {
        if (
            m_opened.TryGetValue(
                key: source.Name,
                value: out var opened
            ) &&
            (opened.Source == source)
        ) {
            window = opened.Window;

            return true;
        }

        window = null;

        return false;
    }
}
