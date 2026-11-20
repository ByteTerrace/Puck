using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// The <c>keyLights</c> document section: what a seat's keyboard lamps show. A keyboard that lights each key is a
/// legend the player already looks at, so a world paints it from its own data: the keys its active binding page binds,
/// keys it names, and the key that types a character held in state. Rows paint in document order over
/// <see cref="Idle"/>, a later row winning a key two rows both light. Presentation only: nothing here reaches
/// simulation.
/// </summary>
/// <param name="Idle">The color of every key no lit row names.</param>
/// <param name="Flash">The color a flashing key starts from when its row authors no flash color of its own.</param>
/// <param name="Modifier">The color of a held chord modifier's key.</param>
/// <param name="UnavailableScale">The brightness, in <c>[0, 1]</c>, a row's color keeps while the row is unavailable.</param>
/// <param name="FlashSeconds">The seconds a flash takes to fade back to the key's own color. Positive, at most
/// <see cref="WorldKeyLightCapacity.MaxFlashSeconds"/>.</param>
/// <param name="Rows">The rows, in paint order.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldKeyLightSection(
    BindableColor Idle,
    BindableColor Flash,
    BindableColor Modifier,
    float UnavailableScale = 0.25f,
    float FlashSeconds = 0.25f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldKeyLightRow>? Rows = null
);
/// <summary>
/// One row of <c>keyLights</c>: a set of keys and the color they take. The keys are the union of what the row names
/// three ways, each optional: <see cref="Keys"/> literally, <see cref="Bound"/> through the seat's active binding page,
/// and <see cref="Character"/> through the key that types it.
/// </summary>
/// <param name="Id">The row's stable id, unique within the section.</param>
/// <param name="Color">The color the row's keys take.</param>
/// <param name="Keys">Keyboard input source ids (<c>keyboard.a</c>, <c>keyboard.space</c>).</param>
/// <param name="Bound">Binding entry ids: the keyboard sources the seat's active page binds each entry to, so the lamps
/// follow a page change and a player's rebinding.</param>
/// <param name="Character">A Unicode scalar value, usually a state cell: the key that types it lights, and a letter
/// lights its key whatever its case. A value no key types (zero included) lights nothing.</param>
/// <param name="Lit">The row paints while this is positive; absent, it always paints.</param>
/// <param name="Available">The row is dimmed by <see cref="WorldKeyLightSection.UnavailableScale"/> while this is not
/// positive; absent, it is never dimmed.</param>
/// <param name="FlashSignal">The row's keys flash each time this changes to a new value after it is first read.</param>
/// <param name="FlashColor">The color the row's flash starts from; absent, the section's
/// <see cref="WorldKeyLightSection.Flash"/>.</param>
/// <param name="Seat">The seat whose keyboard the row lights (<c>seat1</c>..<c>seat4</c>), or
/// <see langword="null"/> for every local seat that holds one.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldKeyLightRow(
    [property: DocumentText("a key light row id")] string Id,
    BindableColor Color,
    [property: DocumentText("input source identifiers")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Keys = null,
    [property: DocumentText("binding entry ids")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Bound = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Character = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Lit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Available = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? FlashSignal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? FlashColor = null,
    [property: DocumentText("a seat selector")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Seat = null
);
/// <summary>The <see cref="WorldKeyLightSection"/> bounds <see cref="WorldDefinitionValidator"/> enforces.</summary>
public static class WorldKeyLightCapacity {
    /// <summary>The most rows the section holds.</summary>
    public const int MaxRows = 64;
    /// <summary>The most keys or binding entries one row names.</summary>
    public const int MaxKeysPerRow = 128;
    /// <summary>The longest fade a flash may take, in seconds.</summary>
    public const float MaxFlashSeconds = 5f;
}
/// <summary>
/// One row of the <c>highlights</c> document section: a run of presented frames a world asks the host to keep. Each time
/// <see cref="Signal"/> changes to a new value after it is first read, the host writes the next <see cref="Frames"/>
/// presented frames as images under its own capture directory, named for the row. A document names no path: where a
/// player's files go is the host's. Presentation only, and a host that composes no frame capture ignores the section.
/// </summary>
/// <param name="Id">The row's stable id, unique within the section, and the name its files carry.</param>
/// <param name="Signal">The state-driven trigger.</param>
/// <param name="Frames">How many presented frames one trigger writes, from 1 to
/// <see cref="WorldHighlightCapacity.MaxFrames"/>.</param>
/// <param name="DelaySeconds">The seconds between the trigger and the first written frame, so a still shows what the
/// trigger led to, from 0 to <see cref="WorldHighlightCapacity.MaxDelaySeconds"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldHighlightRow(
    [property: DocumentText("a highlight row id, the stem of the files it writes")] string Id,
    BindableScalar Signal,
    int Frames = 1,
    float DelaySeconds = 0f
);
/// <summary>The <see cref="WorldHighlightRow"/> bounds <see cref="WorldDefinitionValidator"/> enforces.</summary>
public static class WorldHighlightCapacity {
    /// <summary>The most rows the section holds.</summary>
    public const int MaxRows = 16;
    /// <summary>The most frames one trigger writes.</summary>
    public const int MaxFrames = 120;
    /// <summary>The longest delay between a trigger and its first frame, in seconds.</summary>
    public const float MaxDelaySeconds = 10f;
}
