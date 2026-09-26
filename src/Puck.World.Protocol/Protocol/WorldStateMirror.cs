using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Maths;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>How a mirror slot converts the cell it reads for its consumers.</summary>
public enum WorldStateConversion : byte {
    /// <summary>An Int, Fixed or Bool cell read as a number (Bool reads 0 or 1); any other kind reads no number.</summary>
    Number,

    /// <summary>A Text cell holding a <c>#RRGGBB</c> or <c>#RRGGBBAA</c> color.</summary>
    Color,

    /// <summary>A whole keyed row read as numbers, cell <c>i</c> at element <c>i</c> (<see cref="WorldBoundRow"/>), as
    /// stored or eased per cell by the binding's target flag; an absent or non-numeric cell reads zero.</summary>
    Row,
}
/// <summary>
/// The presentation's state mirror: one flat table of slots, each a bound row ordinal, a cell key, a target flag and
/// a conversion, holding every state read a HUD gauge, camera operand, marker, render color, theme token, binding bar,
/// binding context, radial wheel, look lane, gait driver, pose, effector or body scale makes. It is the one path
/// presentation reads state through.
/// <para>
/// A slot is registered, by the installed document's presentation manifest or by an owner's registered set of reads
/// (<see cref="Register"/>: a seat's binding contexts, radial wheel and binding bar cells), or acquired, by a holder
/// whose reads end — a body that leaves, a seat whose route moves. Registrations and holders are counted, and a slot
/// is retired once it has neither: <see cref="Install"/> retires the slots only the previous document's manifest
/// registered, <see cref="Unregister"/> the slots only its owner's set did, and <see cref="Release"/> the slots only
/// its holder held. A retired slot's index is reused. A consumer finds a registered slot through
/// <see cref="SlotOf(in StateBinding, WorldStateConversion)"/>, a lookup that registers and reads nothing, so every
/// slot a frame reads was registered and read at a tick boundary; a consumer that keeps a slot's index finds it again
/// when <see cref="Generation"/> moves. <see cref="Install"/> resolves every slot to its row ordinal when a document is
/// installed. <see cref="Refresh"/> runs at the tick boundary and reads only the slots whose rows the delivery's stamp names as
/// moved, plus the slots whose cell carries a value-over-time trait that has not come to rest, so a tick that moves
/// nothing reads nothing. <see cref="Apply"/> runs once per frame: a slot whose trait-bearing value moved between the
/// previous tick and this one presents the value interpolated at the frame's fraction between them, and every other
/// slot presents its current sample. A binding reads the eased follower by default and the stored truth with
/// <c>.$target</c>.
/// </para>
/// <para>
/// Refresh and apply both run on the thread that pumps the simulation and presents frames, so the mirror takes no
/// lock. Reads are counted as <c>presentation.mirror.reads</c>.
/// </para>
/// </summary>
public sealed class WorldStateMirror : IWorkCounterSource {
    /// <summary>The stable counter-source name.</summary>
    public const string SourceName = "presentation.mirror";

    /// <summary>Counts every cell read the mirror made through its state view.</summary>
    public static readonly WorkKind Reads = new(
        name: "presentation.mirror.reads",
        unit: "reads",
        workClass: WorkClass.Deterministic
    );

    private static readonly WorkKind[] Kinds = [Reads];
    private readonly Dictionary<string, (bool Parsed, StateBinding Binding)> m_bindingByToken = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<object, WorldPresentationBinding[]> m_registrations = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(StateBinding Binding, WorldStateConversion Conversion), int> m_slotByBinding = [];

    private readonly IWorldStateView m_view;

    private int[] m_firstSlotByOrdinal = [];
    private int[] m_free = [];

    private int m_freeCount;
    private int m_generation;
    private int m_installs;
    private WorldPresentationManifest? m_manifest;

    private int[] m_moving = [];

    private int m_movingCount;

    private int[] m_nextSlotOfOrdinal = [];

    private WorkCount m_reads;
    private int m_refreshSerial;

    private int[] m_restless = [];

    private int m_restlessCount;

    private int[] m_restlessNext = [];

    private int m_revision;
    private int m_colorRevision;

    private Slot[] m_slots = [];

    private int m_slotCount;
    private ulong m_engineTick;
    private ulong m_previousEngineTick;
    private ulong m_tick;
    private bool m_ticked;

    /// <summary>Initializes a new instance of the <see cref="WorldStateMirror"/> class.</summary>
    /// <param name="view">The state view every slot reads through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="view"/> is <see langword="null"/>.</exception>
    public WorldStateMirror(IWorldStateView view) {
        ArgumentNullException.ThrowIfNull(argument: view);

        m_view = view;
    }

    /// <inheritdoc/>
    public string Name => SourceName;
    /// <summary>Gets a counter that moves whenever a refresh or install changes any slot's current sample, so a
    /// consumer caching a value it derived from slots knows to derive it again. <see cref="Changed"/> answers the same
    /// question for one slot.</summary>
    public int Revision => m_revision;
    /// <summary>Gets a counter that moves whenever a refresh or install changes any color slot's color, so a consumer
    /// caching colors it resolved knows to resolve them again without following every moving number.</summary>
    public int ColorRevision => m_colorRevision;
    /// <summary>Gets a counter that moves at every <see cref="Install"/>: a holder that finds its slots by the authored
    /// objects naming them lets them go when it moves, because an installed document carries objects of its own.</summary>
    public int Installs => m_installs;
    /// <summary>Gets a counter that moves whenever the set of registered bindings changes — a binding gains its first
    /// registration or loses its last, at an <see cref="Install"/>, a <see cref="Register"/> or an
    /// <see cref="Unregister"/>: a consumer that keeps what a lookup answered, a slot's index or -1, looks it up again
    /// when this moves, since the index may since have been retired and reused, or the binding registered.</summary>
    public int Generation => m_generation;
    /// <summary>Gets the number of live slots: registered, or acquired and not yet retired.</summary>
    public int SlotCount => (m_slotCount - m_freeCount);
    /// <summary>Gets the tick the slots were last read as of.</summary>
    public ulong Tick => m_tick;
    /// <summary>Gets the engine tick the slots were last read as of: the delivered engine tick a frame presents.</summary>
    public ulong EngineTick => m_engineTick;
    /// <summary>Gets the engine tick the slots were read as of at the refresh before the latest one, which a moving
    /// slot's previous sample holds; it equals <see cref="EngineTick"/> after an install and before any refresh.</summary>
    public ulong PreviousEngineTick => m_previousEngineTick;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => Kinds;
    /// <summary>Gets the presentation manifest of the document the mirror reads, whose bindings it registers at every
    /// <see cref="Install"/> and whose templates a body's lease acquires when the body arrives.</summary>
    public WorldPresentationManifest Manifest => m_view.Manifest;

    /// <summary>Finds the registered slot a binding reads through with a conversion: one the installed document's
    /// presentation manifest or an owner's registered set records. It registers nothing, reads nothing and allocates
    /// nothing, so a binding nothing registered reads no cell.</summary>
    /// <param name="binding">The parsed binding.</param>
    /// <param name="conversion">How the consumer converts the cell.</param>
    /// <returns>The slot's index, valid until <see cref="Generation"/> moves; -1 when no registration records the
    /// binding.</returns>
    public int SlotOf(in StateBinding binding, WorldStateConversion conversion) => ((m_slotByBinding.TryGetValue(
        key: (binding, conversion),
        value: out var slot
    ) && (m_slots[slot].Registrations > 0))
        ? slot
        : -1
    );
    /// <summary>Finds the registered slot an authored binding token reads through, parsing the token once per mirror:
    /// later calls with the same token answer the parse from a table and look the slot up afresh, so a token never
    /// answers with a slot that has since been retired.</summary>
    /// <param name="token">The authored token, which may be a literal or malformed.</param>
    /// <param name="conversion">How the consumer converts the cell.</param>
    /// <returns>The slot's index, or -1 when the token is absent, is no state binding, or no registration records
    /// it.</returns>
    public int SlotOf(string? token, WorldStateConversion conversion) {
        if (token is null) {
            return -1;
        }

        if (!m_bindingByToken.TryGetValue(
            key: token,
            value: out var parse
        )) {
            parse.Parsed = StateBinding.TryParse(
                binding: out parse.Binding,
                token: token
            );
            m_bindingByToken[token] = parse;
        }

        return (parse.Parsed
            ? SlotOf(
                binding: in parse.Binding,
                conversion: conversion
            )
            : -1
        );
    }
    /// <summary>Registers an owner's set of reads beside the installed document's manifest, replacing the set the
    /// owner registered before: a new binding's slot is allocated and read at the mirror's current tick, and a binding
    /// only the replaced set registered stops being registered, retiring its slot when no holder reads it. Registering
    /// the set the owner already holds changes nothing and allocates nothing.</summary>
    /// <param name="owner">The object the set is registered under, compared by reference.</param>
    /// <param name="bindings">The set, which the mirror keeps and the caller must not change.</param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> or <paramref name="bindings"/> is
    /// <see langword="null"/>.</exception>
    public void Register(object owner, WorldPresentationBinding[] bindings) {
        ArgumentNullException.ThrowIfNull(argument: owner);
        ArgumentNullException.ThrowIfNull(argument: bindings);

        _ = m_registrations.TryGetValue(
            key: owner,
            value: out var previous
        );

        if (ReferenceEquals(
            objA: previous,
            objB: bindings
        )) {
            return;
        }

        Mark(
            bindings: bindings,
            read: true
        );
        m_registrations[owner] = bindings;

        if (previous is not null) {
            Unmark(bindings: previous);
        }
    }
    /// <summary>Withdraws an owner's registered set of reads, retiring each slot no other registration records and no
    /// holder reads.</summary>
    /// <param name="owner">The object the set was registered under.</param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    public void Unregister(object owner) {
        ArgumentNullException.ThrowIfNull(argument: owner);

        if (m_registrations.Remove(
            key: owner,
            value: out var previous
        )) {
            Unmark(bindings: previous);
        }
    }
    /// <summary>Returns the slot a binding reads through with a conversion on behalf of one holder, reading it at the
    /// mirror's current tick on first sight. Every acquire is matched by one <see cref="Release"/>; the slot is retired
    /// once its last holder releases it, unless a registration records it.</summary>
    /// <param name="binding">The parsed binding, its key already resolved for the holder.</param>
    /// <param name="conversion">How the consumer converts the cell.</param>
    /// <returns>The slot's index, valid until the holder releases it.</returns>
    public int Acquire(in StateBinding binding, WorldStateConversion conversion) {
        if (!m_slotByBinding.TryGetValue(
            key: (binding, conversion),
            value: out var slot
        )) {
            slot = Allocate(
                binding: in binding,
                conversion: conversion
            );
        }

        m_slots[slot].Holders++;

        return slot;
    }
    /// <summary>Releases one holder's acquisition of a slot, retiring the slot when no holder remains and no
    /// registration reads it: it stops being read, and its index is free for a later binding.</summary>
    /// <param name="slot">The slot's index, from <see cref="Acquire"/>.</param>
    /// <exception cref="InvalidOperationException">The slot has no holder to release.</exception>
    public void Release(int slot) {
        ref var entry = ref m_slots[slot];

        if (entry.Holders <= 0) {
            throw new InvalidOperationException(message: $"State mirror slot {slot} has no holder to release.");
        }

        entry.Holders--;

        if (
            (entry.Holders == 0) &&
            (entry.Registrations == 0)
        ) {
            Retire(slot: slot);
        }
    }
    /// <summary>Resolves every slot to its row ordinal in the newly installed document, registers every binding the
    /// document's presentation manifest (<see cref="IWorldStateView.Manifest"/>) records, retires every slot only the
    /// previous document's manifest registered and no holder reads, and reads every slot. Installing a document whose
    /// manifest the mirror already holds registers nothing new and allocates nothing, and a binding both manifests
    /// record keeps its slot's index.</summary>
    /// <param name="tick">The tick the installed document's values hold as of.</param>
    /// <param name="engineTick">The engine tick the installed document's values hold as of.</param>
    public void Install(ulong tick, ulong engineTick) {
        m_installs++;
        m_tick = tick;
        m_engineTick = engineTick;
        m_previousEngineTick = engineTick;
        m_ticked = true;
        Array.Fill(
            array: m_firstSlotByOrdinal,
            value: -1
        );

        for (var slot = 0; (slot < m_slotCount); slot++) {
            if (m_slots[slot].Free) {
                continue;
            }

            m_slots[slot].Ordinal = -1;
            Link(slot: slot);
        }

        var manifest = m_view.Manifest;

        if (!ReferenceEquals(
            objA: manifest,
            objB: m_manifest
        )) {
            // A manifest slot is linked here and read once by the refresh below, like every slot already held.
            Mark(
                bindings: manifest.Bindings,
                read: false
            );

            if (m_manifest is { } previous) {
                Unmark(bindings: previous.Bindings);
            }

            m_manifest = manifest;
        }

        RefreshSlots(everything: true, moved: default);
    }
    /// <summary>Refreshes the slots a state delivery moved, at the tick boundary: the slots bound to a row the stamp
    /// names, and every slot whose trait-bearing cell has not come to rest.</summary>
    /// <param name="stamp">The delivery's stamp.</param>
    public void Refresh(in WorldStateStamp stamp) {
        m_tick = stamp.Tick;
        MoveEngineTick(engineTick: stamp.EngineTick);
        RefreshSlots(
            everything: stamp.Everything,
            moved: stamp.MovedRows.Span
        );
    }
    /// <summary>Re-reads the slots bound to rows whose values arrive beside the document rather than in it, at the
    /// mirror's current tick: a field row, whose cells a snapshot carries
    /// (<see cref="WorldDocumentStateView.ApplyFieldCells"/>). Such a row's cells never move between writes, so the
    /// read leaves every moving slot's interpolation as it was; a row no slot reads reads nothing.</summary>
    /// <param name="ordinals">The catalog ordinals of the rows whose values moved, each once.</param>
    public void RefreshRows(ReadOnlySpan<int> ordinals) {
        foreach (var ordinal in ordinals) {
            if (((uint)ordinal) >= ((uint)m_firstSlotByOrdinal.Length)) {
                continue;
            }

            for (var slot = m_firstSlotByOrdinal[ordinal]; (slot >= 0); slot = m_nextSlotOfOrdinal[slot]) {
                Read(slot: slot);
            }
        }
    }
    /// <summary>Refreshes the slots whose trait-bearing cell has not come to rest, at a tick no state delivery
    /// refreshed.</summary>
    /// <param name="tick">The completed tick.</param>
    /// <param name="engineTick">The completed engine tick.</param>
    public void Advance(ulong tick, ulong engineTick) {
        if (
            m_ticked &&
            (tick <= m_tick)
        ) {
            return;
        }

        m_tick = tick;
        MoveEngineTick(engineTick: engineTick);
        RefreshSlots(everything: false, moved: default);
    }
    /// <summary>Presents every moving slot at the frame's position between the previous tick and the current one.</summary>
    /// <param name="fraction">The frame's interpolation fraction in <c>[0, 1]</c>; an offscreen capture passes 1.</param>
    public void Apply(float fraction) {
        var clamped = Math.Clamp(
            max: 1f,
            min: 0f,
            value: fraction
        );

        for (var index = 0; (index < m_movingCount); index++) {
            ref var slot = ref m_slots[m_moving[index]];

            slot.Presented = (slot.Previous + ((slot.Current - slot.Previous) * clamped));
        }
    }
    /// <summary>Returns the engine tick a frame presents at a fraction between the previous refresh and the latest one,
    /// the moment <see cref="Apply"/> presents every moving slot at: <see cref="PreviousEngineTick"/> at zero and
    /// <see cref="EngineTick"/> at one. It is the one presentation clock a frame's time is read from, a function of
    /// the delivered ticks and the frame's fraction alone, so an offscreen capture, which pins the fraction to one,
    /// presents exactly the delivered engine tick.</summary>
    /// <param name="fraction">The frame's interpolation fraction in <c>[0, 1]</c>; a value outside it is
    /// clamped.</param>
    /// <returns>The presented engine tick, fractional between refreshes.</returns>
    public double PresentedEngineTick(float fraction) {
        var clamped = Math.Clamp(
            max: 1f,
            min: 0f,
            value: fraction
        );

        return (m_previousEngineTick + ((m_engineTick - m_previousEngineTick) * ((double)clamped)));
    }
    /// <summary>Returns the value <see cref="Revision"/> held when a slot's current sample last changed, so a consumer
    /// that derived something from a known set of slots re-derives it only when one of them moved since the revision it
    /// derived at.</summary>
    /// <param name="slot">The slot's index, or -1, which never changed.</param>
    /// <returns>The revision of the slot's last change; zero when its sample never changed from absent.</returns>
    public int Changed(int slot) => ((slot >= 0)
        ? m_slots[slot].Changed
        : 0
    );
    /// <summary>Reads a slot's presented number.</summary>
    /// <param name="slot">The slot's index, or -1, which reads nothing.</param>
    /// <param name="value">The presented number, or zero when the cell reads none.</param>
    /// <returns><see langword="true"/> when the cell reads a number.</returns>
    public bool TryNumber(int slot, out float value) {
        var resolved = TryValue(
            slot: slot,
            value: out var presented
        );

        value = ((float)presented);

        return resolved;
    }
    /// <summary>Reads a slot's presented number at full precision.</summary>
    /// <param name="slot">The slot's index, or -1, which reads nothing.</param>
    /// <param name="value">The presented number, or zero when the cell reads none.</param>
    /// <returns><see langword="true"/> when the cell reads a number.</returns>
    public bool TryValue(int slot, out double value) {
        if (slot < 0) {
            value = 0d;

            return false;
        }

        ref readonly var entry = ref m_slots[slot];

        value = (entry.HasNumber
            ? entry.Presented
            : 0d
        );

        return entry.HasNumber;
    }
    /// <summary>Reads a row slot's elements (<see cref="WorldStateConversion.Row"/>) as its last refresh read them.</summary>
    /// <param name="slot">The slot's index, or -1, which reads nothing.</param>
    /// <returns>The elements, cell <c>i</c> at element <c>i</c>; empty for -1, a slot of another conversion, or a row
    /// the installed document does not declare.</returns>
    public ReadOnlySpan<double> RowValues(int slot) => (((slot >= 0) && m_slots[slot].HasNumber)
        ? m_slots[slot].Elements
        : default);
    /// <summary>Reads a slot's current text.</summary>
    /// <param name="slot">The slot's index, or -1, which reads nothing.</param>
    /// <param name="value">The cell's text, or <see langword="null"/> when the cell holds no text.</param>
    /// <returns><see langword="true"/> when the cell holds text.</returns>
    public bool TryText(int slot, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out string? value) {
        var sample = Sample(slot: slot).Value;

        value = ((sample.HasValue && (sample.Kind == CellKind.Text))
            ? sample.AsText
            : null
        );

        return (value is not null);
    }
    /// <summary>Reads a slot's current color.</summary>
    /// <param name="slot">The slot's index, or -1, which reads nothing.</param>
    /// <param name="value">The color, or <see cref="Vector4.Zero"/> when the cell holds none.</param>
    /// <returns><see langword="true"/> when the cell holds a color.</returns>
    public bool TryColor(int slot, out Vector4 value) {
        if (slot < 0) {
            value = Vector4.Zero;

            return false;
        }

        ref readonly var entry = ref m_slots[slot];

        value = entry.Color;

        return entry.HasColor;
    }
    /// <summary>Returns a slot's current sample: the cell value read at the current tick, the row's envelope, and its
    /// motion.</summary>
    /// <param name="slot">The slot's index, or -1, which reads nothing.</param>
    /// <returns>The sample; its value holds no case when the slot is -1 or the row or cell does not resolve.</returns>
    public WorldStateSample Sample(int slot) => ((slot >= 0)
        ? m_slots[slot].Sample
        : default
    );
    /// <summary>Resolves a bindable scalar: its literal, or its registered binding's presented number.</summary>
    /// <param name="scalar">The authored scalar.</param>
    /// <param name="fallback">The value when the literal is not finite, no registration records the binding, or it
    /// reads no number.</param>
    /// <returns>The scalar's presented value.</returns>
    public float Scalar(in BindableScalar scalar, float fallback) {
        if (scalar.State is { } binding) {
            return (TryNumber(
                slot: SlotOf(
                    binding: in binding,
                    conversion: WorldStateConversion.Number
                ),
                value: out var bound
            )
                ? bound
                : fallback
            );
        }

        return (((scalar.Literal is { } literal) && float.IsFinite(f: literal))
            ? literal
            : fallback
        );
    }
    /// <summary>Resolves a bindable color: its literal, or its registered binding's current color.</summary>
    /// <param name="color">The authored color.</param>
    /// <param name="fallback">The color when the token is malformed, no registration records the binding, or it holds
    /// no color.</param>
    /// <returns>The color.</returns>
    public Vector4 Color(in BindableColor color, Vector4 fallback) {
        if (color.State is { } binding) {
            return (TryColor(
                slot: SlotOf(
                    binding: in binding,
                    conversion: WorldStateConversion.Color
                ),
                value: out var bound
            )
                ? bound
                : fallback
            );
        }

        return (color.Literal ?? fallback);
    }
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) => WorkCounterSources.TryReadSingle(
        count: in m_reads,
        declared: Reads,
        kind: kind,
        value: out value
    );

    // Moves the delivered engine tick forward, keeping the one it replaces as the tick a moving slot's previous sample
    // holds. An engine tick behind the latest one (a restored checkpoint) starts again from itself.
    private void MoveEngineTick(ulong engineTick) {
        m_previousEngineTick = ((m_ticked && (engineTick >= m_engineTick))
            ? m_engineTick
            : engineTick
        );
        m_engineTick = engineTick;
        m_ticked = true;
    }
    private static void Remove(int[] list, ref int count, int slot) {
        for (var index = 0; (index < count); index++) {
            if (list[index] == slot) {
                list[index] = list[--count];

                return;
            }
        }
    }
    // Counts one more registration of each binding, allocating a slot for a binding no slot reads yet; a binding's first
    // registration moves the generation. read says whether a new slot is read now or by the install's refresh that
    // follows.
    private void Mark(ReadOnlySpan<WorldPresentationBinding> bindings, bool read) {
        foreach (ref readonly var entry in bindings) {
            if (!m_slotByBinding.TryGetValue(
                key: (entry.Binding, entry.Conversion),
                value: out var slot
            )) {
                slot = Allocate(
                    binding: entry.Binding,
                    conversion: entry.Conversion,
                    read: read
                );
            }

            if (m_slots[slot].Registrations++ == 0) {
                m_generation++;
            }
        }
    }
    // Counts one registration fewer of each binding: a slot left with none moves the generation, and retires unless a
    // holder reads it.
    private void Unmark(ReadOnlySpan<WorldPresentationBinding> bindings) {
        foreach (ref readonly var entry in bindings) {
            if (!m_slotByBinding.TryGetValue(
                key: (entry.Binding, entry.Conversion),
                value: out var slot
            )) {
                continue;
            }

            ref var registered = ref m_slots[slot];

            if (--registered.Registrations > 0) {
                continue;
            }

            m_generation++;

            if (registered.Holders == 0) {
                Retire(slot: slot);
            }
        }
    }
    private int Allocate(in StateBinding binding, WorldStateConversion conversion, bool read = true) {
        int slot;

        if (m_freeCount > 0) {
            slot = m_free[--m_freeCount];
        } else {
            if (m_slotCount == m_slots.Length) {
                var capacity = Math.Max(
                    val1: 8,
                    val2: (m_slots.Length * 2)
                );

                Array.Resize(
                    array: ref m_slots,
                    newSize: capacity
                );
                Array.Resize(
                    array: ref m_nextSlotOfOrdinal,
                    newSize: capacity
                );
                Array.Resize(
                    array: ref m_moving,
                    newSize: capacity
                );
                Array.Resize(
                    array: ref m_restless,
                    newSize: capacity
                );
                Array.Resize(
                    array: ref m_restlessNext,
                    newSize: capacity
                );
                Array.Resize(
                    array: ref m_free,
                    newSize: capacity
                );
            }

            slot = m_slotCount++;
        }

        m_slots[slot] = new Slot {
            Binding = binding,
            Conversion = conversion,
            Ordinal = -1,
        };
        m_slotByBinding[(binding, conversion)] = slot;
        Link(slot: slot);

        if (!read) {
            return slot;
        }

        Read(slot: slot);

        if (m_slots[slot].Sample.Motion != WorldStateMotion.Still) {
            m_restless[m_restlessCount++] = slot;
        }

        return slot;
    }
    private void Link(int slot) {
        ref var entry = ref m_slots[slot];

        m_nextSlotOfOrdinal[slot] = -1;

        if (!m_view.TryResolveRow(
            ordinal: out var ordinal,
            rowName: entry.Binding.Row
        )) {
            return;
        }

        if (ordinal >= m_firstSlotByOrdinal.Length) {
            var previous = m_firstSlotByOrdinal.Length;

            Array.Resize(
                array: ref m_firstSlotByOrdinal,
                newSize: Math.Max(
                val1: (ordinal + 1),
                val2: (previous * 2)
            )
            );
            m_firstSlotByOrdinal.AsSpan(start: previous).Fill(value: -1);
        }

        entry.Ordinal = ordinal;
        m_nextSlotOfOrdinal[slot] = m_firstSlotByOrdinal[ordinal];
        m_firstSlotByOrdinal[ordinal] = slot;

        // A row slot's elements are sized once, when the slot is linked to an installed row, so a refresh allocates
        // nothing.
        if (entry.Conversion == WorldStateConversion.Row) {
            var length = m_view.RowLength(ordinal: ordinal);

            if (entry.Elements?.Length != length) {
                entry.Elements = new double[length];
            }
        }
    }
    private void Retire(int slot) {
        ref var entry = ref m_slots[slot];
        var ordinal = entry.Ordinal;

        if (ordinal >= 0) {
            if (m_firstSlotByOrdinal[ordinal] == slot) {
                m_firstSlotByOrdinal[ordinal] = m_nextSlotOfOrdinal[slot];
            } else {
                for (var previous = m_firstSlotByOrdinal[ordinal]; (previous >= 0); previous = m_nextSlotOfOrdinal[previous]) {
                    if (m_nextSlotOfOrdinal[previous] == slot) {
                        m_nextSlotOfOrdinal[previous] = m_nextSlotOfOrdinal[slot];

                        break;
                    }
                }
            }
        }

        _ = m_slotByBinding.Remove(key: (entry.Binding, entry.Conversion));
        Remove(
            count: ref m_movingCount,
            list: m_moving,
            slot: slot
        );
        Remove(
            count: ref m_restlessCount,
            list: m_restless,
            slot: slot
        );
        m_nextSlotOfOrdinal[slot] = -1;
        entry = new Slot {
            Free = true,
            Ordinal = -1,
        };
        m_free[m_freeCount++] = slot;
    }
    private void RefreshSlots(bool everything, ReadOnlySpan<int> moved) {
        m_refreshSerial++;

        // A slot that moved last tick and is not read again this one has come to rest on its current sample.
        for (var index = 0; (index < m_movingCount); index++) {
            ref var slot = ref m_slots[m_moving[index]];

            slot.Previous = slot.Current;
            slot.Presented = slot.Current;
        }

        m_movingCount = 0;

        if (everything) {
            for (var slot = 0; (slot < m_slotCount); slot++) {
                if (!m_slots[slot].Free) {
                    Touch(slot: slot);
                }
            }
        } else {
            foreach (var ordinal in moved) {
                if (((uint)ordinal) >= ((uint)m_firstSlotByOrdinal.Length)) {
                    continue;
                }

                for (var slot = m_firstSlotByOrdinal[ordinal]; (slot >= 0); slot = m_nextSlotOfOrdinal[slot]) {
                    Touch(slot: slot);
                }
            }

            for (var index = 0; (index < m_restlessCount); index++) {
                Touch(slot: m_restless[index]);
            }
        }

        var restless = 0;

        for (var index = 0; (index < m_movingCount); index++) {
            var slot = m_moving[index];

            if (m_slots[slot].Sample.Motion != WorldStateMotion.Still) {
                m_restlessNext[restless++] = slot;
            }
        }

        // Every read slot entered the moving list; keep only those whose presented value moves between ticks.
        var moving = 0;

        for (var index = 0; (index < m_movingCount); index++) {
            var slot = m_moving[index];
            ref var entry = ref m_slots[slot];

            if (
                entry.Interpolates &&
                (entry.Previous != entry.Current)
            ) {
                m_moving[moving++] = slot;
            } else {
                entry.Previous = entry.Current;
                entry.Presented = entry.Current;
            }
        }

        m_movingCount = moving;
        (m_restless, m_restlessNext) = (m_restlessNext, m_restless);
        m_restlessCount = restless;
    }
    // Reads a slot once per refresh, and queues it for the restless and moving passes that follow.
    private void Touch(int slot) {
        ref var entry = ref m_slots[slot];

        if (entry.ReadSerial == m_refreshSerial) {
            return;
        }

        entry.ReadSerial = m_refreshSerial;
        Read(slot: slot);
        m_moving[m_movingCount++] = slot;
    }
    private void Read(int slot) {
        ref var entry = ref m_slots[slot];

        if (entry.Conversion == WorldStateConversion.Row) {
            ReadRow(entry: ref entry);

            return;
        }

        var hadNumber = entry.HasNumber;
        var previousMotion = entry.Sample.Motion;
        var previousValue = entry.Sample.Value;

        entry.Previous = entry.Current;

        if (
            (entry.Ordinal < 0) ||
            !m_view.TryRead(
            engineTick: m_engineTick,
            key: entry.Binding.Key,
            ordinal: entry.Ordinal,
            sample: out var sample,
            target: entry.Binding.Target,
            tick: m_tick
        )
        ) {
            sample = default;
        }

        m_reads.Increment();
        entry.Sample = sample;
        entry.HasNumber = TryConvertNumber(
            number: out var number,
            value: sample.Value
        );
        entry.Current = number;

        // A slot never read, or whose last read held no number, has nothing to interpolate from: it presents its
        // first number whole rather than easing in from zero.
        if (!hadNumber) {
            entry.Previous = entry.Current;
        }

        entry.Interpolates = (
            (sample.Motion is WorldStateMotion.Easing or WorldStateMotion.Advancing) ||
            (previousMotion is WorldStateMotion.Easing or WorldStateMotion.Advancing)
        );
        entry.HasColor = (
            (entry.Conversion == WorldStateConversion.Color) &&
            sample.Value.HasValue &&
            (sample.Value.Kind == CellKind.Text) &&
            HexColor.TryParseRgba(
            rgba: out entry.Color,
            value: sample.Value.AsText
        )
        );
        entry.Presented = entry.Current;

        if (
            (entry.Previous != entry.Current) ||
            !previousValue.Equals(other: sample.Value)
        ) {
            m_revision++;
            entry.Changed = m_revision;

            if (entry.Conversion == WorldStateConversion.Color) {
                m_colorRevision++;
            }
        }
    }
    // Reads every element of a row slot, cell i under the decimal key i, as one read. The slot presents its current
    // elements whole, never interpolated, and stays restless while any cell is still moving.
    private void ReadRow(ref Slot entry) {
        var elements = (entry.Elements ?? []);
        var motion = WorldStateMotion.Still;
        var changed = false;

        for (var index = 0; (index < elements.Length); index++) {
            var number = 0d;

            if (
                (entry.Ordinal >= 0) &&
                m_view.TryRead(
                engineTick: m_engineTick,
                key: IndexKeyCache.Get(index: index),
                ordinal: entry.Ordinal,
                sample: out var sample,
                target: entry.Binding.Target,
                tick: m_tick
            )
            ) {
                _ = TryConvertNumber(
                    number: out number,
                    value: sample.Value
                );
                if (sample.Motion != WorldStateMotion.Still) {
                    motion = sample.Motion;
                }
            }
            if (elements[index] != number) {
                elements[index] = number;
                changed = true;
            }
        }

        m_reads.Increment();
        entry.HasNumber = (entry.Ordinal >= 0);
        entry.Interpolates = false;
        entry.Sample = new WorldStateSample(
            Max: null,
            Min: null,
            Motion: motion,
            Value: default
        );
        if (
            changed ||
            (entry.Changed == 0)
        ) {
            m_revision++;
            entry.Changed = m_revision;
        }
    }
    private static bool TryConvertNumber(CellValue value, out double number) {
        if (!value.HasValue) {
            number = 0d;

            return false;
        }

        switch (value.Kind) {
            case CellKind.Int:
                number = value.AsInt;

                return true;
            case CellKind.Fixed:
                number = ((double)FixedQ4816.FromRawBits(value: value.AsFixed));

                return true;
            case CellKind.Bool:
                number = (value.AsBool
                    ? 1d
                    : 0d
                );

                return true;
            default:
                number = 0d;

                return false;
        }
    }

    private struct Slot {
        public StateBinding Binding;
        public int Changed;
        public Vector4 Color;
        public WorldStateConversion Conversion;
        public double Current;
        public double[]? Elements;
        public bool Free;
        public bool HasColor;
        public bool HasNumber;
        public int Holders;
        public bool Interpolates;
        public int Ordinal;
        public double Presented;
        public double Previous;
        public int ReadSerial;
        public int Registrations;
        public WorldStateSample Sample;
    }
}
