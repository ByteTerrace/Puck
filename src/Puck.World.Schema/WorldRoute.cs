using Puck.Abstractions.Documents;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>A route through the world a seat plays as a player would (<c>routes</c> rows): chapters of steps that say
/// where to go and how, played by the seat program the route names (<see cref="WorldSeatProgram"/>) through the seat's
/// own controls. <c>world.benchmark run &lt;route&gt;</c> plays one in real time and reports each chapter; a chapter
/// fails where a player would see it fail, on a driven contact with a wall or by leaving the places its path
/// crosses.</summary>
/// <param name="Name">The route's name, unique within the document.</param>
/// <param name="Program">The seat program that plays it.</param>
/// <param name="Chapters">The chapters, played in order.</param>
/// <param name="Seat">The 1-based seat that plays it.</param>
/// <param name="Margin">The clearance, in metres, a straightened path keeps from the edge of the walkable ground on top
/// of the walker's own radius the navigation domains already keep.</param>
/// <param name="Captures">The places the route's key views are taken at: after the timed run, a capture pass frames each
/// named place's region from four sides once its bakes settle (<c>world.benchmark marks</c>). Absent takes none.</param>
public sealed record WorldRoute(
    [property: DocumentText("a route's name, which world.benchmark and puck benchmark take")]
    [property: JsonPropertyName("name")] string Name,
    [property: DocumentText("a seat program name")]
    [property: JsonPropertyName("program")] string Program,
    [property: JsonPropertyName("chapters")] IReadOnlyList<WorldRouteChapter> Chapters,
    [property: JsonPropertyName("seat")] int Seat = 1,
    [property: JsonPropertyName("margin")] float Margin = 0.25f,
    [property: DocumentText("a place name, as world.benchmark marks reads it")]
    [property: JsonPropertyName("captures"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Captures = null
);
/// <summary>One chapter of a <see cref="WorldRoute"/>: its steps, played in order, and the time it may take.</summary>
/// <param name="Name">The chapter's name, unique within its route; a report addresses the chapter by it.</param>
/// <param name="Steps">The steps.</param>
/// <param name="TimeoutSeconds">The simulated time after which an unfinished chapter fails.</param>
public sealed record WorldRouteChapter(
    [property: DocumentText("a chapter's name, local to its route")]
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("steps")] IReadOnlyList<WorldRouteStep> Steps,
    [property: JsonPropertyName("timeoutSeconds")] float TimeoutSeconds = 120f
);
/// <summary>One step of a <see cref="WorldRouteChapter"/>. A place is a region placement's id (<c>place-lobby</c>), the
/// volume <c>body.place</c> answers from, in the route's own document unless the step names the world it is played in
/// (<c>world</c>, that world's <c>documentId</c>), as a route that crosses into another world does for that world's
/// places; a heading is a yaw in degrees (0 faces -Z, 90 faces -X).</summary>
[JsonDerivedType(typeof(WorldRouteStep.PoseBody), typeDiscriminator: "pose")]
[JsonDerivedType(typeof(WorldRouteStep.Line), typeDiscriminator: "line")]
[JsonDerivedType(typeof(WorldRouteStep.Go), typeDiscriminator: "go")]
[JsonDerivedType(typeof(WorldRouteStep.Cross), typeDiscriminator: "cross")]
[JsonDerivedType(typeof(WorldRouteStep.Fly), typeDiscriminator: "fly")]
[JsonDerivedType(typeof(WorldRouteStep.Rest), typeDiscriminator: "rest")]
[JsonDerivedType(typeof(WorldRouteStep.PressAction), typeDiscriminator: "press")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldRouteStep {
    private WorldRouteStep() {
    }

    /// <summary>Gets this step's authored <c>$type</c> token.</summary>
    [JsonIgnore]
    public string Opcode => (this switch {
        PoseBody => "pose",
        Line => "line",
        Go => "go",
        Cross => "cross",
        Fly => "fly",
        Rest => "rest",
        PressAction => "press",
        _ => "unknown",
    });

    /// <summary>Sets the seat's body down at a spawn point, or at a point facing a heading (the seat's grant to pose is
    /// the world author's).</summary>
    /// <param name="Spawn">The spawn point's id, or <see langword="null"/> for <paramref name="Point"/>.</param>
    /// <param name="Point">The point, or <see langword="null"/> for <paramref name="Spawn"/>.</param>
    /// <param name="YawDegrees">The heading a point pose faces.</param>
    public sealed record PoseBody(
        [property: DocumentName(WorldNameKind.SpawnPoint, WorldNameRole.Names)]
        [property: JsonPropertyName("spawn"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Spawn = null,
        [property: JsonPropertyName("point"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Point = null,
        [property: JsonPropertyName("yawDegrees")] float YawDegrees = 0f
    ) : WorldRouteStep;
    /// <summary>Submits one console line as the seat (a view choice, <c>player.camera.view first</c>).</summary>
    /// <param name="Text">The line.</param>
    public sealed record Line(
        [property: DocumentText("console command text")]
        [property: JsonPropertyName("text")] string Text
    ) : WorldRouteStep;
    /// <summary>Travels to a place or a point at a pace. Unless <paramref name="Direct"/>, the path is the navigation
    /// domains' route from where the body stands, straightened to the corners a walker turns at and searched again
    /// from wherever the body has got to when it strays from it; the places the path crosses are the step's
    /// corridor, and standing in none of them fails the chapter. A place is reached on entering it, a point within
    /// <paramref name="Within"/>.</summary>
    /// <param name="Place">The place, or <see langword="null"/> for <paramref name="Point"/>.</param>
    /// <param name="Point">The point, or <see langword="null"/> for <paramref name="Place"/>.</param>
    /// <param name="Pace">The pace, a word the route's program reads (<c>walk</c>, <c>run</c>).</param>
    /// <param name="Direct">Whether the body steers straight at the goal with no navigation (a lift car's floor, a
    /// world with no domains), its corridor only <paramref name="Corridor"/>.</param>
    /// <param name="Within">The distance, in metres, at which a point is reached.</param>
    /// <param name="Corridor">Places the body may also stand in on the way.</param>
    /// <param name="World">The <c>documentId</c> of the world the step is played in, whose places
    /// <paramref name="Place"/> and <paramref name="Corridor"/> name, or <see langword="null"/> for the route's own
    /// document. The step fails when the seat stands in another world.</param>
    public sealed record Go(
        [property: DocumentText("a route place or corridor")]
        [property: JsonPropertyName("place"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Place = null,
        [property: JsonPropertyName("point"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Point = null,
        [property: DocumentText("a route pace")]
        [property: JsonPropertyName("pace")] string Pace = "",
        [property: JsonPropertyName("direct")] bool Direct = false,
        [property: JsonPropertyName("within")] float Within = 0.5f,
        [property: DocumentText("a route place or corridor")]
        [property: JsonPropertyName("corridor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Corridor = null,
        [property: DocumentText("a route world")]
        [property: JsonPropertyName("world"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? World = null
    ) : WorldRouteStep;
    /// <summary>Crosses a threshold no navigation domain joins (a portal) or walks where no domain reaches (a world it
    /// crossed into): turns the camera onto a heading, then holds the stick straight ahead, as a player walking through
    /// does, so a crossing that turns the world's axes carries the walk on through it. It ends on entering a place, or
    /// with none after walking a distance.</summary>
    /// <param name="HeadingDegrees">The heading walked: in the frame of the world the step starts in, or with
    /// <paramref name="Relative"/> a turn from the facing the step starts with.</param>
    /// <param name="Until">The place that ends the step, in whichever world the walk reaches, or <see langword="null"/>
    /// to walk <paramref name="Metres"/>.</param>
    /// <param name="Pace">The pace.</param>
    /// <param name="Metres">The distance walked, in metres: the whole step with no <paramref name="Until"/>, otherwise the
    /// distance past which the step fails short of it. A crossing's jump across the world is no distance walked.</param>
    /// <param name="Relative">Whether <paramref name="HeadingDegrees"/> turns from the step's starting facing.</param>
    public sealed record Cross(
        [property: JsonPropertyName("headingDegrees")] float HeadingDegrees,
        [property: DocumentText("a route place or corridor")]
        [property: JsonPropertyName("until"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Until = null,
        [property: DocumentText("a route pace")]
        [property: JsonPropertyName("pace")] string Pace = "",
        [property: JsonPropertyName("metres")] float Metres = 30f,
        [property: JsonPropertyName("relative")] bool Relative = false
    ) : WorldRouteStep;
    /// <summary>Flies to a point through the air, as a player flying a craft or a free-flight body does: the stick held
    /// toward the point over the ground with the camera turned onto the way, and the program's <c>rise</c> controls
    /// (<see cref="WorldSeatProgramOp.Rise"/>) held while the point stands above or below the body along the body's own
    /// up. No path is planned and no corridor checked. The step ends within <paramref name="Within"/> of the point,
    /// on arriving in the world <paramref name="Enters"/> names, when the way crosses a seam into it, or, with
    /// <paramref name="Lands"/>, once the body stands on ground.</summary>
    /// <param name="Point">The point flown to, in the coordinates of the world the step starts in.</param>
    /// <param name="Pace">The pace, a word the route's program reads (a held boost).</param>
    /// <param name="Within">The distance, in metres, at which the point is reached.</param>
    /// <param name="World">The <c>documentId</c> of the world the step starts in, or <see langword="null"/> for wherever
    /// the seat stands. The step fails when the seat starts it in another world.</param>
    /// <param name="Enters">The <c>documentId</c> of the world whose arrival ends the step, or <see langword="null"/>
    /// for a flight that stays in one world. With it, reaching the point without arriving fails the step.</param>
    /// <param name="Lands">Whether the step is a landing: it flies at the point, a point of the ground or under it,
    /// until the body, or the body it rides, is grounded, and <paramref name="Within"/> ends nothing.</param>
    public sealed record Fly(
        [property: JsonPropertyName("point")] DocumentVector3 Point,
        [property: DocumentText("a route pace")]
        [property: JsonPropertyName("pace")] string Pace = "",
        [property: JsonPropertyName("within")] float Within = 10f,
        [property: DocumentText("a route world")]
        [property: JsonPropertyName("world"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? World = null,
        [property: DocumentText("a route world")]
        [property: JsonPropertyName("enters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Enters = null,
        [property: JsonPropertyName("lands")] bool Lands = false
    ) : WorldRouteStep;
    /// <summary>Stands for a time, the camera turned onto a heading or toward a point; with neither it holds the
    /// facing it has.</summary>
    /// <param name="Seconds">The time stood.</param>
    /// <param name="FaceDegrees">The heading faced, or <see langword="null"/>.</param>
    /// <param name="LookAt">The point looked toward, or <see langword="null"/>.</param>
    public sealed record Rest(
        [property: JsonPropertyName("seconds")] float Seconds,
        [property: JsonPropertyName("faceDegrees"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? FaceDegrees = null,
        [property: JsonPropertyName("lookAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? LookAt = null
    ) : WorldRouteStep;
    /// <summary>Presses an action where the body stands (the program's button for it), then stands until the body is in
    /// a place the world carries it to (a lift arriving) or, with none, for <paramref name="Seconds"/>.</summary>
    /// <param name="Action">The action, a word the route's program maps to a button.</param>
    /// <param name="Until">The place that ends the wait, or <see langword="null"/>.</param>
    /// <param name="Seconds">The time stood after the press with no <paramref name="Until"/>, or the time past which
    /// the wait for it fails.</param>
    /// <param name="World">The <c>documentId</c> of the world whose place <paramref name="Until"/> names, or
    /// <see langword="null"/> for the route's own document.</param>
    public sealed record PressAction(
        [property: DocumentText("a seat program action")]
        [property: JsonPropertyName("action")] string Action,
        [property: DocumentText("a route place or corridor")]
        [property: JsonPropertyName("until"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Until = null,
        [property: JsonPropertyName("seconds")] float Seconds = 1f,
        [property: DocumentText("a route world")]
        [property: JsonPropertyName("world"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? World = null
    ) : WorldRouteStep;
}
