using System.Globalization;

namespace Puck.World.Client;

/// <summary>Mints the names of the offscreen view registrations the engine creates beside the ones an author names:
/// a session screen's view and a seat-relative camera's per-seat view, the world producer of each view past the first
/// (<see cref="World"/>), and the names the synthesized root graph declares for itself (<see cref="Root"/>). Each is a
/// generated document name (<see cref="GeneratedName.Join"/>), and an authored camera name may not be in that form, so
/// no minted view name can equal a camera's own registration. The parts are recoverable: a session view is <c>session$&lt;screen&gt;</c>, two
/// parts; a seat view is <c>&lt;camera&gt;$seat$&lt;seat&gt;</c>, three parts, the camera first because it is the
/// name the view belongs to and the seat last because it is the qualifier that varies.</summary>
public static class WorldViewNames {
    /// <summary>The first part of a session screen's view name.</summary>
    public const string SessionHead = "session";
    /// <summary>The part between a camera's name and the seat number in a seat-relative camera's view name.</summary>
    public const string SeatPart = "seat";

    /// <summary>Returns the view name a session-sourced screen registers its view under.</summary>
    /// <param name="screen">The 0-based index of the screen the session feeds.</param>
    /// <returns><c>session$&lt;screen&gt;</c>.</returns>
    public static string Session(int screen) => GeneratedName.Join(
        SessionHead,
        screen.ToString(provider: CultureInfo.InvariantCulture)
    );
    /// <summary>Returns the view name a seat-relative camera registers under for one seat.</summary>
    /// <param name="camera">The camera's name; free of <see cref="GeneratedName.Joiner"/> past its first
    /// character, which the world validator holds of every camera name.</param>
    /// <param name="seat">The 1-based seat.</param>
    /// <returns><c>&lt;camera&gt;$seat$&lt;seat&gt;</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="camera"/> is empty or carries the joiner past its first
    /// character.</exception>
    public static string Seat(string camera, int seat) => GeneratedName.Join(
        camera,
        SeatPart,
        seat.ToString(provider: CultureInfo.InvariantCulture)
    );
    /// <summary>Returns the name of the render-graph instance that produces one view past the first:
    /// <c>world$&lt;view&gt;</c>, the world producer's name joined with the 1-based view. The first view is the world
    /// producer itself (<see cref="WorldViewGraphs.WorldInstance"/>).</summary>
    /// <param name="view">The 1-based view, at least 2.</param>
    /// <returns><c>world$&lt;view&gt;</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="view"/> is below 2.</exception>
    public static string World(int view) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 2,
            value: view
        );

        return GeneratedName.Join(
            WorldViewGraphs.WorldInstance,
            view.ToString(provider: CultureInfo.InvariantCulture)
        );
    }
    /// <summary>Returns the 0-based view whose producer an instance name names (<see cref="World"/>'s inverse), or
    /// <see langword="null"/> for any other name, the first view's producer included.</summary>
    /// <param name="instance">The instance name.</param>
    /// <returns>The 0-based view, at least 1, or <see langword="null"/>.</returns>
    public static int? ViewOf(string instance) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        var prefix = (WorldViewGraphs.WorldInstance + GeneratedName.Joiner);

        return ((
            instance.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix) &&
            int.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var view,
                s: instance.AsSpan(start: prefix.Length),
                style: NumberStyles.None
            ) &&
            (view >= 2) &&
            string.Equals(a: World(view: view), b: instance, comparisonType: StringComparison.Ordinal)
        )
            ? (view - 1)
            : null);
    }
    /// <summary>Returns the name of a version or pass the synthesized root graph declares for itself, joined under the
    /// root's instance name: <c>main$world</c>, <c>main$stage$1</c>, <c>main$post$&lt;id&gt;$2</c>. A pane's version
    /// and its place pass take the pane's authored name, which an author may not spell in this form, so no name the
    /// root declares for itself can equal a pane's.</summary>
    /// <param name="parts">One or more parts, none empty and none carrying <see cref="GeneratedName.Joiner"/>.</param>
    /// <returns><c>main$&lt;part&gt;…</c>.</returns>
    /// <exception cref="ArgumentException">No part is given, or a part is empty or carries the joiner.</exception>
    public static string Root(params ReadOnlySpan<string> parts) {
        var joined = new string[(parts.Length + 1)];

        joined[0] = WorldViewGraphs.MainInstance;
        parts.CopyTo(destination: joined.AsSpan(start: 1));

        return GeneratedName.Join(parts: joined);
    }
}
