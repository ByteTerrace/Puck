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
/// <para>A source stays opened while its pane's capture keeps showing the window it showed when it was opened. When the
/// instance stops running, its capture reopens onto another window, or a row of the same name replaces it, the source is
/// closed the next time input would reach it, so a window the local user did not open never receives input.</para>
/// <para>Window-pump-thread only: the console's immediate verbs and the pump's events run there.</para>
/// </remarks>
internal sealed class WorldSourcePassthrough : IWindowInputFilter, ISourcePassthroughWindows {
    private readonly WorldScreenBinder m_binder;
    private readonly WorldViewGraphHost m_graphs;
    private readonly SortedDictionary<string, ISourcePassthroughWindow> m_opened = new(comparer: StringComparer.Ordinal);
    private readonly WorldSeatViewports m_viewports;

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

    /// <summary>Gets the opened sources' instance names and windows, by name.</summary>
    public IReadOnlyDictionary<string, ISourcePassthroughWindow> Opened => m_opened;
    /// <summary>Gets the router that hosts the keyboard focus.</summary>
    public SourcePassthroughRouter Router { get; }

    /// <summary>Opens a pane's capture as a passthrough source the local user opened.</summary>
    /// <param name="instance">The instance a shown pane draws.</param>
    /// <param name="windowTitle">The title the local user names the window by; the window the pane captures must carry
    /// it, compared without regard to case.</param>
    /// <param name="message">What was opened, or why nothing was.</param>
    /// <returns><see langword="true"/> when the source is open.</returns>
    public bool TryOpen(string instance, string windowTitle, out string message) {
        if (!m_graphs.TryGetPane(
            instance: instance,
            mapping: out _
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

        m_opened[instance] = window;
        m_graphs.OpenPassthrough(instance: instance);
        message = $"'{instance}' opened as a passthrough source for the window '{title}'; a click on its pane gives it the keyboard, and Control+Alt+Escape returns it to the game";

        return true;
    }
    /// <summary>Closes a passthrough source, returning the keyboard to the game when it had it.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns><see langword="true"/> when the source was open.</returns>
    public bool Close(string instance) {
        if (!m_opened.Remove(key: instance)) {
            return false;
        }

        _ = m_graphs.ClosePassthrough(instance: instance);

        if ((Router.Focus.Focused is { } focused) && string.Equals(
            a: focused.Name,
            b: instance,
            comparisonType: StringComparison.Ordinal
        )) {
            Router.Focus.ReturnToGame();
        }

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

        foreach (var (instance, window) in m_opened) {
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
        if (!m_opened.TryGetValue(
            key: source.Name,
            value: out window
        )) {
            return false;
        }
        if (ReferenceEquals(
            objA: m_binder.PassthroughWindowOf(
                fault: out _,
                instance: source.Name
            ),
            objB: window
        )) {
            return true;
        }

        // The pane no longer shows the window the local user opened.
        _ = Close(instance: source.Name);
        window = null;

        return false;
    }
}
