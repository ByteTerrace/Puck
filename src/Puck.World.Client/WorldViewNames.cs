using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Sources;

namespace Puck.World.Client;

/// <summary>Mints the names of the offscreen view registrations the engine creates beside the ones an author names:
/// a session screen's view and a seat-relative camera's per-seat view, a screen source's render-graph instance
/// (<see cref="Source"/>, <c>source$&lt;producer&gt;$&lt;digest&gt;</c>), the world producer of each view past the
/// first (<see cref="World"/>, <c>world$&lt;view&gt;</c>), and the names the synthesized root graph declares for itself
/// (<see cref="Root"/>). Each is a generated document name (<see cref="GeneratedName.Join"/>), and an authored camera
/// name may not be in that form, so no minted view name can equal a camera's own registration. The parts are
/// recoverable: a session view is <c>session$&lt;screen&gt;</c>, two
/// parts, and a session seen through it adds the screen of the world it shows that it stands on
/// (<see cref="Nested"/>, <c>session$&lt;screen&gt;$&lt;screen&gt;…</c>), one part a level; the sessions a world seats
/// are presented in shows are <c>routed$&lt;digest&gt;$&lt;screen&gt;…</c> (<see cref="Routed"/>); a seat view is
/// <c>&lt;camera&gt;$seat$&lt;seat&gt;</c>, three parts, the camera first because it is the
/// name the view belongs to and the seat last because it is the qualifier that varies.</summary>
public static class WorldViewNames {
    /// <summary>The first part of a session screen's view name.</summary>
    public const string SessionHead = "session";
    /// <summary>The part between a camera's name and the seat number in a seat-relative camera's view name.</summary>
    public const string SeatPart = "seat";
    /// <summary>The first part of a source instance's name.</summary>
    public const string SourceHead = "source";
    /// <summary>The first part of the name under which a world seats are presented in names the sessions its screens
    /// show.</summary>
    public const string RoutedHead = "routed";

    /// <summary>Returns the view name a session-sourced screen registers its view under.</summary>
    /// <param name="screen">The 0-based index of the screen the session feeds.</param>
    /// <returns><c>session$&lt;screen&gt;</c>.</returns>
    public static string Session(int screen) => GeneratedName.Join(
        SessionHead,
        screen.ToString(provider: CultureInfo.InvariantCulture)
    );
    /// <summary>Returns the view name of a session a screen shows inside the world another view renders: the view's name
    /// joined with the screen's index, so every level of nesting names its views from the level above, and the same
    /// screen seen at two depths is two views.</summary>
    /// <param name="view">The name of the view whose world the screen stands in: a session's
    /// (<see cref="Session"/>, or a nested one) or a routed world's head (<see cref="Routed"/>).</param>
    /// <param name="screen">The 0-based index of the screen in that world.</param>
    /// <returns><c>&lt;view&gt;$&lt;screen&gt;</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="view"/> is empty.</exception>
    public static string Nested(string view, int screen) => GeneratedName.Append(
        name: view,
        part: screen.ToString(provider: CultureInfo.InvariantCulture)
    );
    /// <summary>Returns the head the sessions of a world seats are presented in are named under: the digest of the
    /// identity of the authority the world runs on under <see cref="RoutedHead"/>, so a screen of that world shows
    /// <c>routed$&lt;digest&gt;$&lt;screen&gt;</c> (<see cref="Nested"/>). An identity is any text, a remote one included,
    /// so the name carries 16 hex characters of its SHA-256 rather than the identity itself, the same for one identity
    /// on every run.</summary>
    /// <param name="authority">The identity of the authority the world runs on.</param>
    /// <returns><c>routed$&lt;digest&gt;</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    public static string Routed(string authority) {
        ArgumentNullException.ThrowIfNull(argument: authority);

        return GeneratedName.Join(
            RoutedHead,
            Convert.ToHexStringLower(
                inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: authority)),
                length: 8,
                offset: 0
            )
        );
    }
    /// <summary>Returns the name of the source instance a producer, machine or probe source is read through, named by its
    /// content: its producer and the digest of its settings' canonical form (<see cref="ImageSourceSettings.Digest"/>).
    /// Every screen showing equal sources reads the one instance of that name, a screen added, removed or reordered
    /// renames no other source, and a settings change is a new name.</summary>
    /// <param name="producer">The producer's registered id, free of <see cref="GeneratedName.Joiner"/>.</param>
    /// <param name="settings">The source's settings object, or <see langword="null"/> for none.</param>
    /// <returns><c>source$&lt;producer&gt;$&lt;digest&gt;</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="producer"/> is empty or carries the joiner, or a setting
    /// holds an undefined value.</exception>
    public static string Source(string producer, IReadOnlyDictionary<string, JsonElement>? settings) => GeneratedName.Join(
        SourceHead,
        producer,
        ImageSourceSettings.Digest(settings: settings)
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
    /// <see langword="null"/> for any other name, the first view's producer included. It allocates nothing, so a frame
    /// may ask it of every instance it reads.</summary>
    /// <param name="instance">The instance name.</param>
    /// <returns>The 0-based view, at least 1, or <see langword="null"/>.</returns>
    public static int? ViewOf(string instance) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        var head = WorldViewGraphs.WorldInstance.Length;

        // World(view) writes the view without a sign or a leading zero, so only that spelling is its inverse.
        return ((
            (instance.Length > (head + 1)) &&
            instance.StartsWith(comparisonType: StringComparison.Ordinal, value: WorldViewGraphs.WorldInstance) &&
            (instance[head] == GeneratedName.Joiner) &&
            (instance[(head + 1)] != '0') &&
            int.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var view,
                s: instance.AsSpan(start: (head + 1)),
                style: NumberStyles.None
            ) &&
            (view >= 2)
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
