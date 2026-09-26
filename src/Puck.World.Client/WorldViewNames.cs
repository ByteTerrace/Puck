using System.Globalization;

namespace Puck.World.Client;

/// <summary>Mints the names of the offscreen view registrations the engine creates beside the ones an author names:
/// a session screen's view and a seat-relative camera's per-seat view, a screen source's render-graph instance
/// (<see cref="Source"/>, <c>source$&lt;screen&gt;</c>), and the names the synthesized root graph declares for itself
/// (<see cref="Root"/>). Each is a generated document name
/// (<see cref="GeneratedName.Join"/>), and an authored camera name may not be in that form, so no minted view name can
/// equal a camera's own registration. The parts are recoverable: a session view is <c>session$&lt;screen&gt;</c>, two
/// parts; a seat view is <c>&lt;camera&gt;$seat$&lt;seat&gt;</c>, three parts, the camera first because it is the
/// name the view belongs to and the seat last because it is the qualifier that varies.</summary>
public static class WorldViewNames {
    /// <summary>The first part of a session screen's view name.</summary>
    public const string SessionHead = "session";
    /// <summary>The part between a camera's name and the seat number in a seat-relative camera's view name.</summary>
    public const string SeatPart = "seat";
    /// <summary>The first part of a source instance's name.</summary>
    public const string SourceHead = "source";

    /// <summary>Returns the view name a session-sourced screen registers its view under.</summary>
    /// <param name="screen">The 0-based index of the screen the session feeds.</param>
    /// <returns><c>session$&lt;screen&gt;</c>.</returns>
    public static string Session(int screen) => GeneratedName.Join(
        SessionHead,
        screen.ToString(provider: CultureInfo.InvariantCulture)
    );
    /// <summary>Returns the name of the source instance a screen's producer, machine or probe source is read through,
    /// named after the first screen showing it, so every later screen showing the same source reads the same
    /// instance.</summary>
    /// <param name="screen">The 0-based index of the first screen showing the source.</param>
    /// <returns><c>source$&lt;screen&gt;</c>.</returns>
    public static string Source(int screen) => GeneratedName.Join(
        SourceHead,
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
