using Puck.Abstractions.Documents;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A named deterministic device whose lifetime is independent of its output consumers.</summary>
/// <param name="Name">The instance identity used by displays, controls, and hardware operations.</param>
/// <param name="Engine">The registered provider identifier.</param>
/// <param name="Configuration">The provider's versioned configuration, validated through its descriptor.</param>
/// <param name="Running">Whether the host advances this instance. A stopped instance retains its hardware state.</param>
/// <param name="Memory">Ordered hardware bindings, independent of any display.</param>
/// <param name="Cable">The optional standing cable endpoint owned by this machine instance.</param>
/// <param name="ContentFolders">The folders holding the content this device can swap to, in the order they are
/// listed and searched, each relative to the declaring document (<see cref="WorldDocumentPaths"/>), or
/// <see langword="null"/> for a device with no swappable content. A folder that does not exist on this machine is
/// listed as absent, never refused, so a world can name a local folder that only some machines hold.
/// <c>machine.content</c> lists the files in them the device's engine can mount (<see cref="WorldMachineContentFolder"/>)
/// and <c>machine.swap</c> mounts one by name; nothing outside the folders is listed. Omitted from the wire when
/// null.</param>
/// <param name="PreferFolderDefault">Whether the device boots the default its <see cref="ContentFolders"/> name, when
/// one names a file it can mount, in place of its configuration's authored content. A folder names its default in its
/// own <c>folder.json</c> (<see cref="WorldMachineContentFolder.SettingsFile"/>), which the folder's owner writes
/// (<c>machine.default</c>) and the boot and every <c>world.reload</c> read, so the default lives beside the content
/// and no document names it. A device with no default set, or whose default is absent or unmountable, boots its
/// authored content. Omitted from the wire when false.</param>
/// <param name="RunsWhen">The state condition the host advances this instance under, or <see langword="null"/> to
/// advance it on every tick <paramref name="Running"/> allows. The tick evaluates it against the arena after the rule
/// frame folds and before the machines step (<c>Server.WorldTick.GateMachines</c>), so a rule's write starts or stops
/// the machine on the tick it lands, and a replay or a restored checkpoint derives the same gate from the same state:
/// the gate is never stored. A gated-off instance keeps its hardware state, as a stopped one does, and costs no
/// emulation while it waits. It reuses <see cref="WorldPlacementResponseCondition.StateCondition"/>, the reading
/// placement responses, binding overlays and portals share. Omitted from the wire when null.</param>
/// <param name="ContentBy">The state cell that selects which title of the machine's library it runs, or
/// <see langword="null"/> for a machine whose content only its configuration, a folder default and operations decide.
/// The library is the files of <see cref="ContentFolders"/> the engine can mount, in their listing order
/// (<see cref="WorldMachineContentFolder.TryTitle"/>): the cell holding 0 runs the machine's own content, and n runs
/// the library's n-th title. The tick reads the cell after the rule frame folds, beside <paramref name="RunsWhen"/>
/// (<c>Server.WorldTick.GateMachines</c>), and the host mounts a new selection on the tick it lands, so a rule that
/// inserts a cartridge swaps the machine's content, a replay derives the same swaps from the same state, and no
/// document mutation or provider operation is involved. The document always names the authored content
/// (<c>CaptureInstances</c> reports it), so a title's name never enters the document, a capture or a tape. A
/// selection naming no mountable title runs the machine's own content. Omitted from the wire when null.</param>
/// <param name="Attention">How far the machine's attention reaches, or <see langword="null"/> for a machine the host
/// advances on every tick <paramref name="Running"/> and <paramref name="RunsWhen"/> allow, attended or not. A machine
/// that authors one is advanced only on the ticks it is attended (<see cref="WorldMachineAttention"/>) and is suspended
/// on every other: its clock stops, it keeps its hardware state, and it resumes from that state on the tick it is
/// attended again. The tick decides it beside <paramref name="RunsWhen"/> (<c>Server.WorldTick.GateMachines</c>), from
/// authority state alone, so a replay or a restored checkpoint derives the same ticks: the verdict is never stored.
/// Omitted from the wire when null.</param>
public sealed record WorldMachine(
    [property: DocumentName(WorldNameKind.Machine, WorldNameRole.Declares)] string Name,
    [property: DocumentText("a machine provider id")] string Engine,
    JsonElement Configuration,
    bool Running = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMachineMemory>? Memory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMachineCable? Cable = null,
    [property: DocumentText("a path or document locator")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ContentFolders = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool PreferFolderDefault = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementResponseCondition.StateCondition? RunsWhen = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMachineContentSelector? ContentBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMachineAttention? Attention = null
);
/// <summary>How far a machine's attention reaches (<see cref="WorldMachine.Attention"/>). A machine is attended on a
/// tick while a participant holds a control application on a screen that shows it (someone is engaged with it or
/// playing it), or while a body a human occupies or possesses stands within <paramref name="Within"/> of one of those screens and,
/// where <paramref name="Behind"/> is authored, on the side its picture faces.</summary>
/// <remarks>The distances are measured in the world's own fixed-point coordinates, from each occupied body's position
/// to the screen's place (its authored origin, or the body holding a screen a placement carries), so the reading is an
/// authority fact: no camera, frustum or presented frame enters it, and a world with no presentation suspends and
/// resumes its machines on the same ticks as one with a window. A cable link's members are attended together: the
/// link advances while anyone attends any of them.</remarks>
/// <param name="Within">The distance in metres, finite and positive.</param>
/// <param name="Behind">How far behind a screen's face a body still attends it, in metres, finite and at least zero,
/// or <see langword="null"/> for a machine attended from every side. A screen's face looks along its
/// <see cref="WorldScreen.Right"/> crossed with its <see cref="WorldScreen.Up"/> (<see cref="WorldScreen.FixedFacing"/>),
/// and a body stands behind it by the distance from its position to the face's plane, so an allowance of about a
/// body's height keeps a body beside or just behind the screen attending it. A screen a placement carries is attended
/// by its holder whichever way it faces. Omitted from the wire when null.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldMachineAttention(
    float Within,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Behind = null
);
/// <summary>The state cell a machine's <see cref="WorldMachine.ContentBy"/> reads: a declared numeric
/// <c>state.world</c> row, and the cell inside it when the row is keyed.</summary>
/// <param name="State">The state row read; any declared numeric row (never <see cref="CellKind.Text"/>).</param>
/// <param name="Key">The cell inside <paramref name="State"/>, or <see langword="null"/> for its slot cell; refused when
/// the row is keyed and this is absent, or unkeyed and this is present.</param>
/// <param name="WrapFrom">The first title of the library's tail that a selection past the library's end folds into, or
/// <see langword="null"/> for none: a selection n at or past it runs title <c>WrapFrom + (n - WrapFrom) mod
/// (count - WrapFrom + 1)</c>, so a title inside the tail runs as itself and any larger number lands on one. A world
/// that does not know how many titles a local folder holds draws a large number (a seeded draw) and lets the library
/// decide; a library with no title at or past it runs the machine's own content, as any unmatched selection does.
/// Refused below 1.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldMachineContentSelector(
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)] string State,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Key)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WrapFrom = null
) {
    /// <summary>Folds a selection into the library's tail as <see cref="WrapFrom"/> says.</summary>
    /// <param name="title">The selected title, as <see cref="Read"/> returns it.</param>
    /// <param name="titleCount">How many titles the machine's library holds (<see cref="WorldMachineContentFolder.TitleCount"/>).</param>
    /// <returns>The title to run: <paramref name="title"/> itself unless it lies past the library's end at or past a
    /// <see cref="WrapFrom"/> the library reaches.</returns>
    public int Fold(int title, int titleCount) {
        if ((WrapFrom is not { } from) || (title < from) || (title <= titleCount) || (titleCount < from)) {
            return title;
        }

        return (from + ((title - from) % ((titleCount - from) + 1)));
    }

    /// <summary>Reads the title the cell selects: its whole value, or 0 when the cell is absent or negative.</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="tick">The tick to read the cell as of.</param>
    /// <param name="engineTick">The engine tick to read the cell as of.</param>
    /// <param name="arena">The live store, or declaration seeds at admission.</param>
    /// <returns>The selected title, 0 for the machine's own content.</returns>
    public int Read(WorldDefinition definition, ulong tick, ulong engineTick, StateArena? arena = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return (WorldStateReader.TryReadWhole(arena: arena, definition: definition, engineTick: engineTick, key: Key, rowName: State, tick: tick, whole: out var whole)
            ? (int)Math.Clamp(value: whole, min: 0L, max: int.MaxValue)
            : 0);
    }
}
