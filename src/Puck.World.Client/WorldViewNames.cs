using System.Globalization;

namespace Puck.World.Client;

/// <summary>Mints the names of the offscreen view registrations the engine creates beside the ones an author names:
/// a session screen's view and a seat-relative camera's per-seat view. Each is a generated document name
/// (<see cref="GeneratedName.Join"/>), and an authored camera name may not be in that form, so no minted view name can
/// equal a camera's own registration. The parts are recoverable: a session view is <c>session$&lt;screen&gt;</c>, two
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
}
