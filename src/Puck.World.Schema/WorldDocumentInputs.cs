using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Puck.World;

/// <summary>One part of a <see cref="WorldDefinition"/> a compiled product can be built from: a root member, or one
/// part of a root member that holds several (the kits, looks, placements and state sections). Reading a member's
/// property notes its parts (<see cref="WorldDocumentReads"/>), and <see cref="WorldDocumentInputs.Changed"/> names
/// the parts two documents hold as different instances.</summary>
public enum WorldDocumentInput : byte {
    /// <summary>The <c>motion</c> member.</summary>
    Motion,
    /// <summary>The <c>spawnPoints</c> member.</summary>
    SpawnPoints,
    /// <summary>The <c>render</c> member.</summary>
    Render,
    /// <summary>The <c>screens</c> member.</summary>
    Screens,
    /// <summary>The <c>screenMaterials</c> member.</summary>
    ScreenMaterials,
    /// <summary>The <c>cameras</c> member.</summary>
    Cameras,
    /// <summary>The <c>bodies</c> member.</summary>
    Bodies,
    /// <summary>The <c>seatDefaults</c> member.</summary>
    SeatDefaults,
    /// <summary>The <c>channels</c> member.</summary>
    Channels,
    /// <summary>The <c>inputModes</c> member.</summary>
    InputModes,
    /// <summary>The <c>inputHistory</c> member.</summary>
    InputHistory,
    /// <summary>The <c>inputPatterns</c> member.</summary>
    InputPatterns,
    /// <summary>The <c>targetRegisters</c> member.</summary>
    TargetRegisters,
    /// <summary>The <c>bodyMotionPrograms</c> member.</summary>
    BodyMotionPrograms,
    /// <summary>The rows of the <c>kits</c> member.</summary>
    KitRows,
    /// <summary>The assignment of the <c>kits</c> member.</summary>
    KitAssignment,
    /// <summary>The <c>defaultSeatKit</c> member.</summary>
    DefaultSeatKit,
    /// <summary>The <c>addons</c> member.</summary>
    Addons,
    /// <summary>The <c>bindingOverlays</c> member.</summary>
    BindingOverlays,
    /// <summary>The <c>storage</c> member.</summary>
    Storage,
    /// <summary>The <c>prototypes</c> member, the creations.</summary>
    Prototypes,
    /// <summary>The rows of the <c>placements</c> member.</summary>
    PlacementRows,
    /// <summary>The policy of the <c>placements</c> member.</summary>
    PlacementPolicy,
    /// <summary>The <c>speakers</c> member.</summary>
    Speakers,
    /// <summary>The <c>tunes</c> member.</summary>
    Tunes,
    /// <summary>The <c>patches</c> member.</summary>
    Patches,
    /// <summary>The <c>audio</c> member.</summary>
    Audio,
    /// <summary>The <c>collision</c> member.</summary>
    Collision,
    /// <summary>The <c>gravity</c> member.</summary>
    Gravity,
    /// <summary>The <c>host</c> member.</summary>
    Host,
    /// <summary>The <c>views</c> member.</summary>
    Views,
    /// <summary>The rows of the <c>looks</c> member.</summary>
    LookRows,
    /// <summary>The assignment of the <c>looks</c> member.</summary>
    LookAssignment,
    /// <summary>The <c>dynamics</c> member.</summary>
    Dynamics,
    /// <summary>The <c>grants</c> member.</summary>
    Grants,
    /// <summary>The <c>hud</c> member.</summary>
    Hud,
    /// <summary>The <c>icons</c> member.</summary>
    Icons,
    /// <summary>The world rows of the <c>state</c> member.</summary>
    StateWorld,
    /// <summary>The body slots of the <c>state</c> member.</summary>
    StateBody,
    /// <summary>The identity slots of the <c>state</c> member.</summary>
    StateIdentity,
    /// <summary>The lattice topologies of the <c>state</c> member.</summary>
    StateLattices,
    /// <summary>The vector spaces of the <c>state</c> member.</summary>
    StateSpaces,
    /// <summary>The enums of the <c>state</c> member.</summary>
    StateEnums,
    /// <summary>The families of the <c>state</c> member.</summary>
    StateFamilies,
    /// <summary>The records of the <c>state</c> member.</summary>
    StateRecords,
    /// <summary>The pools of the <c>state</c> member.</summary>
    StatePools,
    /// <summary>The pair pools of the <c>state</c> member.</summary>
    StatePairPools,
    /// <summary>The private draw sites the <c>state</c> member has allocated.</summary>
    StateDrawInstallCount,
    /// <summary>The <c>inputHold</c> member.</summary>
    InputHold,
    /// <summary>The <c>theme</c> member.</summary>
    Theme,
    /// <summary>The <c>markers</c> member.</summary>
    Markers,
    /// <summary>The <c>haptics</c> member.</summary>
    Haptics,
    /// <summary>The <c>rules</c> member.</summary>
    Rules,
    /// <summary>The <c>identity</c> member.</summary>
    Identity,
    /// <summary>The <c>groups</c> member.</summary>
    Groups,
    /// <summary>The <c>properties</c> member.</summary>
    Properties,
    /// <summary>The <c>interactions</c> member.</summary>
    Interactions,
    /// <summary>The <c>generation</c> member.</summary>
    Generation,
    /// <summary>The <c>generators</c> member.</summary>
    Generators,
    /// <summary>The <c>references</c> member.</summary>
    References,
    /// <summary>The <c>portals</c> member.</summary>
    Portals,
    /// <summary>The <c>simulation</c> member.</summary>
    Simulation,
    /// <summary>The <c>destinations</c> member.</summary>
    Destinations,
    /// <summary>The <c>admission</c> member.</summary>
    Admission,
    /// <summary>The <c>adjacencies</c> member.</summary>
    Adjacencies,
    /// <summary>The <c>text</c> member.</summary>
    Text,
    /// <summary>The <c>metadata</c> member.</summary>
    Metadata,
    /// <summary>The <c>update</c> member.</summary>
    Update,
    /// <summary>The <c>music</c> member.</summary>
    Music,
    /// <summary>The <c>seatModes</c> member.</summary>
    SeatModes,
    /// <summary>The <c>probes</c> member.</summary>
    Probes,
    /// <summary>The <c>captures</c> member.</summary>
    Captures,
    /// <summary>The <c>schedule</c> member.</summary>
    Schedule,
    /// <summary>The <c>curves</c> member.</summary>
    Curves,
    /// <summary>The <c>navigation</c> member.</summary>
    Navigation,
    /// <summary>The <c>patterns</c> member.</summary>
    Patterns,
    /// <summary>The <c>tables</c> member.</summary>
    Tables,
    /// <summary>The <c>search</c> member.</summary>
    Search,
    /// <summary>The <c>machines</c> member.</summary>
    Machines,
    /// <summary>The <c>ruleGroups</c> member.</summary>
    RuleGroups,
    /// <summary>The <c>sets</c> member.</summary>
    Sets,
    /// <summary>The <c>editor</c> member.</summary>
    Editor,
    /// <summary>The <c>timeline</c> member.</summary>
    Timeline,
    /// <summary>The <c>commands</c> member.</summary>
    Commands,
    /// <summary>The <c>replays</c> member.</summary>
    Replays,
    /// <summary>The <c>roles</c> member.</summary>
    Roles,
    /// <summary>The <c>seatPrograms</c> member.</summary>
    SeatPrograms,
    /// <summary>The <c>routes</c> member.</summary>
    Routes,
    /// <summary>The shade table the document carried.</summary>
    Shades,
    /// <summary>The <c>basis</c> member.</summary>
    Basis,
    /// <summary>The directory relative paths resolve beside.</summary>
    DocumentDirectory,
    /// <summary>Whether the document was read from a file.</summary>
    ReadFromFile,
    /// <summary>The <c>documentId</c> member.</summary>
    DocumentId,
    /// <summary>The <c>exports</c> member.</summary>
    Exports,
    /// <summary>The unknown members the document carried.</summary>
    Extensions,
    /// <summary>The <c>imports</c> member.</summary>
    Imports,
    /// <summary>The <c>schema</c> member.</summary>
    Schema,
}

/// <summary>A set of <see cref="WorldDocumentInput"/> parts: what a compiled product was built from, or what one
/// document changed of another.</summary>
public readonly struct WorldDocumentInputs : IEquatable<WorldDocumentInputs> {
    private readonly ulong m_low;
    private readonly ulong m_high;

    private WorldDocumentInputs(ulong low, ulong high) {
        m_low = low;
        m_high = high;
    }

    /// <summary>The number of parts a document has.</summary>
    public const int Count = ((int)WorldDocumentInput.Schema + 1);

    /// <summary>Gets the empty set.</summary>
    public static WorldDocumentInputs None => default;
    /// <summary>Gets every part of a document: what a whole-document install changes, and what a product that has
    /// not been built yet is taken to read.</summary>
    public static WorldDocumentInputs All { get; } = Range(first: 0, count: Count);
    /// <summary>Gets the two parts of the <c>kits</c> member.</summary>
    public static WorldDocumentInputs Kits { get; } = Of(WorldDocumentInput.KitRows, WorldDocumentInput.KitAssignment);
    /// <summary>Gets the two parts of the <c>looks</c> member.</summary>
    public static WorldDocumentInputs Looks { get; } = Of(WorldDocumentInput.LookRows, WorldDocumentInput.LookAssignment);
    /// <summary>Gets the two parts of the <c>placements</c> member.</summary>
    public static WorldDocumentInputs Placements { get; } = Of(WorldDocumentInput.PlacementRows, WorldDocumentInput.PlacementPolicy);
    /// <summary>Gets the parts of the <c>state</c> member.</summary>
    public static WorldDocumentInputs State { get; } = Range(first: (int)WorldDocumentInput.StateWorld, count: (((int)WorldDocumentInput.StateDrawInstallCount - (int)WorldDocumentInput.StateWorld) + 1));

    /// <summary>Gets whether the set holds no part.</summary>
    public bool IsEmpty => ((m_low | m_high) == 0UL);

    /// <summary>Returns the set holding the given parts.</summary>
    /// <param name="inputs">The parts.</param>
    /// <returns>The set.</returns>
    public static WorldDocumentInputs Of(params ReadOnlySpan<WorldDocumentInput> inputs) {
        var set = None;

        foreach (var input in inputs) { set = set.With(input: input); }
        return set;
    }
    private static WorldDocumentInputs Range(int first, int count) {
        var set = None;

        for (var index = first; (index < (first + count)); index++) { set = set.With(input: (WorldDocumentInput)index); }
        return set;
    }

    /// <summary>Returns this set with one more part.</summary>
    /// <param name="input">The part.</param>
    /// <returns>The set.</returns>
    public WorldDocumentInputs With(WorldDocumentInput input) => ((int)input < 64)
        ? new WorldDocumentInputs(low: (m_low | (1UL << (int)input)), high: m_high)
        : new WorldDocumentInputs(low: m_low, high: (m_high | (1UL << ((int)input - 64))));
    /// <summary>Returns whether the set holds a part.</summary>
    /// <param name="input">The part.</param>
    /// <returns>Whether it is held.</returns>
    public bool Contains(WorldDocumentInput input) => ((int)input < 64)
        ? ((m_low & (1UL << (int)input)) != 0UL)
        : ((m_high & (1UL << ((int)input - 64))) != 0UL);
    /// <summary>Returns whether this set and another hold a part in common.</summary>
    /// <param name="other">The other set.</param>
    /// <returns>Whether they meet.</returns>
    public bool Overlaps(WorldDocumentInputs other) => (((m_low & other.m_low) | (m_high & other.m_high)) != 0UL);
    /// <summary>Returns the parts this set and another hold in common.</summary>
    /// <param name="other">The other set.</param>
    /// <returns>The intersection.</returns>
    public WorldDocumentInputs Intersect(WorldDocumentInputs other) => new(low: (m_low & other.m_low), high: (m_high & other.m_high));
    /// <summary>Returns the parts this set holds and another does not.</summary>
    /// <param name="other">The other set.</param>
    /// <returns>The difference.</returns>
    public WorldDocumentInputs Except(WorldDocumentInputs other) => new(low: (m_low & ~other.m_low), high: (m_high & ~other.m_high));
    /// <summary>Returns the parts either set holds.</summary>
    /// <param name="left">One set.</param>
    /// <param name="right">The other set.</param>
    /// <returns>The union.</returns>
    public static WorldDocumentInputs operator |(WorldDocumentInputs left, WorldDocumentInputs right) => new(low: (left.m_low | right.m_low), high: (left.m_high | right.m_high));
    /// <summary>Returns whether two sets hold the same parts.</summary>
    /// <param name="left">One set.</param>
    /// <param name="right">The other set.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(WorldDocumentInputs left, WorldDocumentInputs right) => left.Equals(other: right);
    /// <summary>Returns whether two sets hold different parts.</summary>
    /// <param name="left">One set.</param>
    /// <param name="right">The other set.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(WorldDocumentInputs left, WorldDocumentInputs right) => !left.Equals(other: right);
    /// <inheritdoc/>
    public bool Equals(WorldDocumentInputs other) => ((m_low == other.m_low) && (m_high == other.m_high));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is WorldDocumentInputs other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(value1: m_low, value2: m_high);

    /// <summary>Returns the parts in the set, by the names a document spells them with, in document order.</summary>
    /// <returns>The names, comma separated; <c>nothing</c> for the empty set.</returns>
    public override string ToString() {
        if (IsEmpty) { return "nothing"; }
        if (this == All) { return "everything"; }

        var text = new StringBuilder();

        for (var index = 0; (index < Count); index++) {
            if (!Contains(input: (WorldDocumentInput)index)) { continue; }
            if (text.Length > 0) { _ = text.Append(value: ", "); }
            _ = text.Append(value: WorldDocumentInputTable.NameOf(input: (WorldDocumentInput)index));
        }
        return text.ToString();
    }

    /// <summary>Returns the parts <paramref name="after"/> holds as another instance than <paramref name="before"/>
    /// does, or, for a value-typed or text part, as an unequal value.</summary>
    /// <remarks>A document change shares every part it leaves alone with the document it was made from (a mutation's
    /// <c>with</c>, a reload's merge over the live document), so a part held as another instance is a change. Two
    /// equal parts held as different instances count as changed, which costs a rebuild and never stands a product
    /// stale.</remarks>
    /// <param name="before">The document a product was built from, or the live document.</param>
    /// <param name="after">The candidate.</param>
    /// <returns>The changed parts.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldDocumentInputs Changed(WorldDefinition before, WorldDefinition after) {
        ArgumentNullException.ThrowIfNull(argument: before);
        ArgumentNullException.ThrowIfNull(argument: after);

        if (ReferenceEquals(objA: before, objB: after)) { return None; }

        WorldDocumentInputTable.Verify();

        var changed = None;

        using (WorldDocumentReads.Unrecorded()) {
            foreach (var (input, read) in WorldDocumentInputTable.Selectors) {
                var left = read(arg: before);
                var right = read(arg: after);

                if (!ReferenceEquals(objA: left, objB: right) && !((left is string or ValueType) && left.Equals(obj: right))) {
                    changed = changed.With(input: input);
                }
            }
        }
        return changed;
    }
}

/// <summary>Records the parts of a <see cref="WorldDefinition"/> a product build reads: every member property notes
/// its parts here, and a build run inside <see cref="Begin"/> learns exactly what it read.</summary>
/// <remarks>A recording belongs to the thread that began it: a build that read the document on another thread would
/// miss those reads, so no product build forks document reads to workers. Recordings nest, and an inner recording's
/// reads join the outer one's, so a product built from another product depends on what that one read.</remarks>
public static class WorldDocumentReads {
    // How many recordings are open on any thread: a getter's only cost while no product builds.
    private static int OpenCount;
    [ThreadStatic] private static Frame? Top;

    /// <summary>Notes that the calling thread read one part of a document.</summary>
    /// <param name="input">The part.</param>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static void Note(WorldDocumentInput input) {
        if (Volatile.Read(location: ref OpenCount) != 0) { NoteOpen(inputs: WorldDocumentInputs.None.With(input: input)); }
    }
    /// <summary>Notes that the calling thread read several parts of a document.</summary>
    /// <param name="inputs">The parts.</param>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static void Note(WorldDocumentInputs inputs) {
        if (Volatile.Read(location: ref OpenCount) != 0) { NoteOpen(inputs: inputs); }
    }
    private static void NoteOpen(WorldDocumentInputs inputs) {
        if (Top is { } top) { top.Inputs |= inputs; }
    }

    /// <summary>Begins recording what the calling thread reads of any document until the recording ends.</summary>
    /// <returns>The recording; <see cref="Recording.End"/> answers what was read.</returns>
    public static Recording Begin() {
        WorldDocumentInputTable.Verify();

        return BeginUnverified();
    }
    // The table's own check records through here, before the table it checks is known to be sound.
    internal static Recording BeginUnverified() {
        var frame = new Frame(parent: Top, discards: false);

        Top = frame;
        _ = Interlocked.Increment(location: ref OpenCount);
        return new Recording(frame: frame);
    }
    /// <summary>Records <paramref name="build"/>'s reads.</summary>
    /// <typeparam name="T">The product.</typeparam>
    /// <param name="build">The build.</param>
    /// <param name="inputs">What it read.</param>
    /// <returns>The product.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="build"/> is <see langword="null"/>.</exception>
    public static T Record<T>(Func<T> build, out WorldDocumentInputs inputs) {
        ArgumentNullException.ThrowIfNull(argument: build);

        var recording = Begin();
        T product;

        try {
            product = build();
        } finally {
            inputs = recording.End();
        }
        return product;
    }
    /// <summary>Suspends the calling thread's recording, for reads that compare documents rather than build from
    /// them.</summary>
    /// <returns>The scope that resumes it.</returns>
    public static Recording Unrecorded() {
        var frame = new Frame(parent: Top, discards: true);

        Top = frame;
        return new Recording(frame: frame);
    }

    /// <summary>An open recording.</summary>
    public readonly struct Recording : IDisposable {
        private readonly Frame? m_frame;

        internal Recording(Frame frame) => m_frame = frame;

        /// <summary>Ends the recording and returns what it read; an inner recording's reads join the one around
        /// it.</summary>
        /// <returns>The parts read.</returns>
        /// <exception cref="InvalidOperationException">Recordings ended out of order.</exception>
        public WorldDocumentInputs End() {
            if (m_frame is not { } frame) { return WorldDocumentInputs.None; }
            if (!ReferenceEquals(objA: Top, objB: frame)) { throw new InvalidOperationException(message: "Document-read recordings ended out of order."); }
            if (frame.Ended) { return frame.Inputs; }
            frame.Ended = true;
            Top = frame.Parent;
            if (frame.Discards) { return WorldDocumentInputs.None; }
            _ = Interlocked.Decrement(location: ref OpenCount);
            if (frame.Parent is { } parent) { parent.Inputs |= frame.Inputs; }
            return frame.Inputs;
        }
        /// <summary>Ends the recording.</summary>
        public void Dispose() => _ = End();
    }

    internal sealed class Frame(Frame? parent, bool discards) {
        public Frame? Parent { get; } = parent;
        public bool Discards { get; } = discards;
        public bool Ended { get; set; }
        public WorldDocumentInputs Inputs { get; set; }
    }
}

// What each part is called, how a comparison reads it, and which parts each property of WorldDefinition notes. The
// first use proves the three agree: every property that stores a part is listed here and notes exactly the parts
// listed, and every part's comparison reads a property noting that part alone. A member added to the document
// without a property that notes it, or noted as the wrong part, fails here at the first build or comparison.
internal static class WorldDocumentInputTable {
    private static readonly string[] Names = BuildNames();
    private static readonly Lazy<bool> Verified = new(valueFactory: VerifyCore, mode: LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<(WorldDocumentInput Input, Func<WorldDefinition, object?> Read)> Selectors { get; } = [
        (WorldDocumentInput.Motion, static d => d.MotionRaw),
        (WorldDocumentInput.SpawnPoints, static d => d.SpawnPointsRaw),
        (WorldDocumentInput.Render, static d => d.RenderRaw),
        (WorldDocumentInput.Screens, static d => d.ScreensRaw),
        (WorldDocumentInput.ScreenMaterials, static d => d.ScreenMaterialsRaw),
        (WorldDocumentInput.Cameras, static d => d.CamerasRaw),
        (WorldDocumentInput.Bodies, static d => d.PopulationRaw),
        (WorldDocumentInput.SeatDefaults, static d => d.PlayerDefaultsRaw),
        (WorldDocumentInput.Channels, static d => d.ChannelsRaw),
        (WorldDocumentInput.InputModes, static d => d.InputModes),
        (WorldDocumentInput.InputHistory, static d => d.InputHistory),
        (WorldDocumentInput.InputPatterns, static d => d.InputPatterns),
        (WorldDocumentInput.TargetRegisters, static d => d.TargetRegistersRaw),
        (WorldDocumentInput.BodyMotionPrograms, static d => d.BodyMotionProgramsRaw),
        (WorldDocumentInput.KitRows, static d => d.KitRowsRaw),
        (WorldDocumentInput.KitAssignment, static d => d.AssignmentRaw),
        (WorldDocumentInput.DefaultSeatKit, static d => d.DefaultSeatKitRaw),
        (WorldDocumentInput.Addons, static d => d.AddonsRaw),
        (WorldDocumentInput.BindingOverlays, static d => d.BindingOverlaysRaw),
        (WorldDocumentInput.Storage, static d => d.StorageRaw),
        (WorldDocumentInput.Prototypes, static d => d.CreationsRaw),
        (WorldDocumentInput.PlacementRows, static d => d.PlacementRowsRaw),
        (WorldDocumentInput.PlacementPolicy, static d => d.AuthoringRaw),
        (WorldDocumentInput.Speakers, static d => d.SpeakersRaw),
        (WorldDocumentInput.Tunes, static d => d.TunesRaw),
        (WorldDocumentInput.Patches, static d => d.PatchesRaw),
        (WorldDocumentInput.Audio, static d => d.AudioRaw),
        (WorldDocumentInput.Collision, static d => d.CollisionRaw),
        (WorldDocumentInput.Gravity, static d => d.GravityRaw),
        (WorldDocumentInput.Host, static d => d.HostRaw),
        (WorldDocumentInput.Views, static d => d.ViewsRaw),
        (WorldDocumentInput.LookRows, static d => d.LookRowsRaw),
        (WorldDocumentInput.LookAssignment, static d => d.LookAssignmentRaw),
        (WorldDocumentInput.Dynamics, static d => d.DynamicsRaw),
        (WorldDocumentInput.Grants, static d => d.GrantsRaw),
        (WorldDocumentInput.Hud, static d => d.HudRaw),
        (WorldDocumentInput.Icons, static d => d.IconsRaw),
        (WorldDocumentInput.StateWorld, static d => d.StatePart(input: WorldDocumentInput.StateWorld)),
        (WorldDocumentInput.StateBody, static d => d.StatePart(input: WorldDocumentInput.StateBody)),
        (WorldDocumentInput.StateIdentity, static d => d.StatePart(input: WorldDocumentInput.StateIdentity)),
        (WorldDocumentInput.StateLattices, static d => d.StatePart(input: WorldDocumentInput.StateLattices)),
        (WorldDocumentInput.StateSpaces, static d => d.StatePart(input: WorldDocumentInput.StateSpaces)),
        (WorldDocumentInput.StateEnums, static d => d.StatePart(input: WorldDocumentInput.StateEnums)),
        (WorldDocumentInput.StateFamilies, static d => d.StatePart(input: WorldDocumentInput.StateFamilies)),
        (WorldDocumentInput.StateRecords, static d => d.StatePart(input: WorldDocumentInput.StateRecords)),
        (WorldDocumentInput.StatePools, static d => d.StatePart(input: WorldDocumentInput.StatePools)),
        (WorldDocumentInput.StatePairPools, static d => d.StatePart(input: WorldDocumentInput.StatePairPools)),
        (WorldDocumentInput.StateDrawInstallCount, static d => d.StatePart(input: WorldDocumentInput.StateDrawInstallCount)),
        (WorldDocumentInput.InputHold, static d => d.InputHoldRaw),
        (WorldDocumentInput.Theme, static d => d.ThemeRaw),
        (WorldDocumentInput.Markers, static d => d.MarkersRaw),
        (WorldDocumentInput.Haptics, static d => d.HapticsRaw),
        (WorldDocumentInput.Rules, static d => d.Rules),
        (WorldDocumentInput.Identity, static d => d.Identity),
        (WorldDocumentInput.Groups, static d => d.Groups),
        (WorldDocumentInput.Properties, static d => d.Properties),
        (WorldDocumentInput.Interactions, static d => d.Interactions),
        (WorldDocumentInput.Generation, static d => d.Generation),
        (WorldDocumentInput.Generators, static d => d.Generators),
        (WorldDocumentInput.References, static d => d.References),
        (WorldDocumentInput.Portals, static d => d.Portals),
        (WorldDocumentInput.Simulation, static d => d.Simulation),
        (WorldDocumentInput.Destinations, static d => d.Destinations),
        (WorldDocumentInput.Admission, static d => d.Admission),
        (WorldDocumentInput.Adjacencies, static d => d.Adjacencies),
        (WorldDocumentInput.Text, static d => d.Text),
        (WorldDocumentInput.Metadata, static d => d.Metadata),
        (WorldDocumentInput.Update, static d => d.Update),
        (WorldDocumentInput.Music, static d => d.Music),
        (WorldDocumentInput.SeatModes, static d => d.SeatModesRaw),
        (WorldDocumentInput.Probes, static d => d.ProbesRaw),
        (WorldDocumentInput.Captures, static d => d.Captures),
        (WorldDocumentInput.Schedule, static d => d.Schedule),
        (WorldDocumentInput.Curves, static d => d.CurvesRaw),
        (WorldDocumentInput.Navigation, static d => d.NavigationRaw),
        (WorldDocumentInput.Patterns, static d => d.PatternsRaw),
        (WorldDocumentInput.Tables, static d => d.Tables),
        (WorldDocumentInput.Search, static d => d.SearchRaw),
        (WorldDocumentInput.Machines, static d => d.MachinesRaw),
        (WorldDocumentInput.RuleGroups, static d => d.RuleGroupsRaw),
        (WorldDocumentInput.Sets, static d => d.SetsRaw),
        (WorldDocumentInput.Editor, static d => d.EditorRaw),
        (WorldDocumentInput.Timeline, static d => d.TimelineRaw),
        (WorldDocumentInput.Commands, static d => d.CommandsRaw),
        (WorldDocumentInput.Replays, static d => d.ReplaysRaw),
        (WorldDocumentInput.Roles, static d => d.RolesRaw),
        (WorldDocumentInput.SeatPrograms, static d => d.SeatProgramsRaw),
        (WorldDocumentInput.Routes, static d => d.RoutesRaw),
        (WorldDocumentInput.Shades, static d => d.StoredShades),
        (WorldDocumentInput.Basis, static d => d.Basis),
        (WorldDocumentInput.DocumentDirectory, static d => d.DocumentDirectory),
        (WorldDocumentInput.ReadFromFile, static d => d.ReadFromFile),
        (WorldDocumentInput.DocumentId, static d => d.DocumentId),
        (WorldDocumentInput.Exports, static d => d.Exports),
        (WorldDocumentInput.Extensions, static d => d.Extensions),
        (WorldDocumentInput.Imports, static d => d.Imports),
        (WorldDocumentInput.Schema, static d => d.Schema),
    ];

    // The parts each property that stores (or writes through to) a part notes when read. The JSON names are the
    // property names a document spells; the parts under kits, looks, placements and state carry their member's name.
    private static readonly Dictionary<string, WorldDocumentInputs> PropertyInputs = BuildPropertyInputs();
    private static readonly HashSet<string> Computed = new(collection: ["ShadesRaw"], comparer: StringComparer.Ordinal);

    public static string NameOf(WorldDocumentInput input) => Names[(int)input];

    public static void Verify() {
        if (!Verified.Value) { throw new InvalidOperationException(message: "The world document's read table failed its check."); }
    }

    private static string[] BuildNames() {
        var names = new string[WorldDocumentInputs.Count];

        for (var index = 0; (index < names.Length); index++) {
            var name = ((WorldDocumentInput)index).ToString();

            names[index] = (char.ToLowerInvariant(c: name[0]) + name[1..]);
        }
        names[(int)WorldDocumentInput.KitRows] = "kits.rows";
        names[(int)WorldDocumentInput.KitAssignment] = "kits.assignment";
        names[(int)WorldDocumentInput.LookRows] = "looks.rows";
        names[(int)WorldDocumentInput.LookAssignment] = "looks.assignment";
        names[(int)WorldDocumentInput.PlacementRows] = "placements.rows";
        names[(int)WorldDocumentInput.PlacementPolicy] = "placements.policy";
        names[(int)WorldDocumentInput.StateWorld] = "state.world";
        names[(int)WorldDocumentInput.StateBody] = "state.body";
        names[(int)WorldDocumentInput.StateIdentity] = "state.identity";
        names[(int)WorldDocumentInput.StateLattices] = "state.lattices";
        names[(int)WorldDocumentInput.StateSpaces] = "state.spaces";
        names[(int)WorldDocumentInput.StateEnums] = "state.enums";
        names[(int)WorldDocumentInput.StateFamilies] = "state.families";
        names[(int)WorldDocumentInput.StateRecords] = "state.records";
        names[(int)WorldDocumentInput.StatePools] = "state.pools";
        names[(int)WorldDocumentInput.StatePairPools] = "state.pairPools";
        names[(int)WorldDocumentInput.StateDrawInstallCount] = "state.drawInstallCount";
        names[(int)WorldDocumentInput.Prototypes] = "prototypes";
        names[(int)WorldDocumentInput.Bodies] = "bodies";
        names[(int)WorldDocumentInput.SeatDefaults] = "seatDefaults";
        return names;
    }

    private static Dictionary<string, WorldDocumentInputs> BuildPropertyInputs() {
        var inputs = new Dictionary<string, WorldDocumentInputs>(comparer: StringComparer.Ordinal) {
            ["KitsRaw"] = WorldDocumentInputs.Kits,
            ["KitRowsRaw"] = WorldDocumentInputs.Of(WorldDocumentInput.KitRows),
            ["AssignmentRaw"] = WorldDocumentInputs.Of(WorldDocumentInput.KitAssignment),
            ["LooksRaw"] = WorldDocumentInputs.Looks,
            ["LookRowsRaw"] = WorldDocumentInputs.Of(WorldDocumentInput.LookRows),
            ["LookAssignmentRaw"] = WorldDocumentInputs.Of(WorldDocumentInput.LookAssignment),
            ["PlacementsRaw"] = WorldDocumentInputs.Placements,
            ["PlacementRowsRaw"] = WorldDocumentInputs.Of(WorldDocumentInput.PlacementRows),
            ["AuthoringRaw"] = WorldDocumentInputs.Of(WorldDocumentInput.PlacementPolicy),
            ["StateRaw"] = WorldDocumentInputs.State,
            ["ShadesRaw"] = WorldDocumentInputs.Of(WorldDocumentInput.Shades),
        };

        // Every other stored member is its own part, named by the selector that reads it.
        foreach (var (input, _) in Selectors) {
            if (WorldDocumentInputs.Kits.Contains(input: input) || WorldDocumentInputs.Looks.Contains(input: input) ||
                WorldDocumentInputs.Placements.Contains(input: input) || WorldDocumentInputs.State.Contains(input: input) || (input == WorldDocumentInput.Shades)) {
                continue;
            }
            inputs.Add(key: SelectedProperty(input: input), value: WorldDocumentInputs.Of(input));
        }
        return inputs;
    }

    // The property each simple part's selector reads, by the property naming convention the record follows.
    private static string SelectedProperty(WorldDocumentInput input) => input switch {
        WorldDocumentInput.Bodies => "PopulationRaw",
        WorldDocumentInput.SeatDefaults => "PlayerDefaultsRaw",
        WorldDocumentInput.Prototypes => "CreationsRaw",
        WorldDocumentInput.InputModes or WorldDocumentInput.InputHistory or WorldDocumentInput.InputPatterns or WorldDocumentInput.Rules or
            WorldDocumentInput.Identity or WorldDocumentInput.Groups or WorldDocumentInput.Properties or WorldDocumentInput.Interactions or
            WorldDocumentInput.Generation or WorldDocumentInput.Generators or WorldDocumentInput.References or WorldDocumentInput.Portals or
            WorldDocumentInput.Simulation or WorldDocumentInput.Destinations or WorldDocumentInput.Admission or WorldDocumentInput.Adjacencies or
            WorldDocumentInput.Text or WorldDocumentInput.Metadata or WorldDocumentInput.Update or WorldDocumentInput.Music or
            WorldDocumentInput.Captures or WorldDocumentInput.Schedule or WorldDocumentInput.Tables or WorldDocumentInput.Basis or
            WorldDocumentInput.DocumentDirectory or WorldDocumentInput.ReadFromFile or WorldDocumentInput.DocumentId or
            WorldDocumentInput.Exports or WorldDocumentInput.Extensions or WorldDocumentInput.Imports or WorldDocumentInput.Schema => input.ToString(),
        _ => (input + "Raw"),
    };

    [UnconditionalSuppressMessage(category: "Trimming", checkId: "IL2075", Justification = "The check reads the properties of WorldDefinition, which DefinitionType keeps.")]
    private static bool VerifyCore() {
        var probe = new WorldDefinition();
        var problems = new List<string>();

        // Every part is read by exactly one selector, and each selector notes its own part alone.
        var seen = new bool[WorldDocumentInputs.Count];

        foreach (var (input, read) in Selectors) {
            if (seen[(int)input]) { problems.Add(item: $"part '{NameOf(input: input)}' has two comparisons"); }
            seen[(int)input] = true;

            var recording = WorldDocumentReads.BeginUnverified();

            _ = read(arg: probe);
            if (recording.End() != WorldDocumentInputs.Of(input)) { problems.Add(item: $"the comparison of part '{NameOf(input: input)}' does not read a property noting that part alone"); }
        }
        for (var index = 0; (index < seen.Length); index++) {
            if (!seen[index]) { problems.Add(item: $"part '{NameOf(input: (WorldDocumentInput)index)}' has no comparison"); }
        }

        // Every property that stores or writes through to a part is listed, and notes exactly its listed parts.
        foreach (var property in DefinitionType.GetProperties(bindingAttr: (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))) {
            if ((property.SetMethod is null) || (property.GetIndexParameters().Length != 0)) { continue; }
            if (!PropertyInputs.TryGetValue(key: property.Name, value: out var expected)) {
                problems.Add(item: $"WorldDefinition.{property.Name} stores a document part but no part names it");
                continue;
            }

            var recording = WorldDocumentReads.BeginUnverified();

            _ = property.GetValue(obj: probe);
            var noted = recording.End();

            // A computed getter (the shade table, derived from the uses) notes what it computes from besides its own part.
            var agrees = (Computed.Contains(item: property.Name) ? (noted.Intersect(other: expected) == expected) : (noted == expected));

            if (!agrees) { problems.Add(item: $"reading WorldDefinition.{property.Name} notes {noted}, not {expected}"); }
        }
        if (problems.Count > 0) {
            throw new InvalidOperationException(message: $"The world document's read table disagrees with WorldDefinition: {string.Join(separator: "; ", values: problems)}.");
        }
        return true;
    }

    [DynamicallyAccessedMembers(memberTypes: (DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties))]
    private static readonly Type DefinitionType = typeof(WorldDefinition);
}
