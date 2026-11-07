using System.Runtime.CompilerServices;
using Puck.Commands;
using Puck.Assets.Documents;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>One state read the presentation manifest records: a parsed binding and how its reader converts the cell.
/// It is the key a <see cref="WorldStateMirror"/> slot is found by.</summary>
/// <param name="Binding">The parsed binding.</param>
/// <param name="Conversion">How the reader converts the cell.</param>
public readonly record struct WorldPresentationBinding(StateBinding Binding, WorldStateConversion Conversion);
/// <summary>
/// The presentation manifest: every state read a document's presentation makes, compiled once from the document and
/// deduplicated. <see cref="Bindings"/> are the reads that last as long as the document, which a
/// <see cref="WorldStateMirror"/> registers when it installs the document: a HUD element's binding or template
/// placeholder, an overlay <c>state</c> predicate, a binding bar's layout and model cells, every bindable scalar and
/// color (camera program operands, markers, render lighting, sky and environment colors, the theme), every state
/// clock's row (read eased, as every keyed value on it reads it), every color a signed-distance program bakes (a creation palette's surface, fill, bleed,
/// weathering and inset colors, a height field's color, a text screen's ink), and each height field's row read whole,
/// which its brick is baked from. <see cref="BodyBindings"/> are templates a body reads through its own
/// <c>WorldStateLease</c>: the population's scale row, a look's pose references and lane operands, a creation
/// driver's state signal, gate tokens and scalar expressions, and an effector's gate tokens and state target. A template keeps its
/// <see cref="StateBinding.BodyKey"/> key as authored; a body's lease resolves that key to the body's index when it
/// acquires the template, so every body reads its own slot of one table, and the slot is released when the body leaves.
/// Each template is also recorded under the document object that carries it (<see cref="TemplatesOf"/>): the
/// document itself for the population's scale row, which every body reads, a <see cref="WorldLook"/> for its motion's
/// reads, and a
/// <see cref="WorldPrototype"/> for its creation's drivers and effectors, so a body's lease acquires exactly the
/// templates of what it wears when it arrives. What a seat composes rather than any one document authors — its binding
/// contexts, radial wheel cells and binding bar cells — is compiled per seat by <see cref="SeatBindings"/>.
/// <para>
/// A surface is found by the type that carries it, wherever the document places that type, so a section that gains a
/// bindable member is covered without a new walker: the manifest is a consumer of the document walk
/// (<see cref="WorldDocumentWalker"/>), which enters only members whose type can reach a surface, so a document's state
/// rows, rules and literal-only sections are never read. A direction or a vector binds no state cell, so neither is a
/// surface; a keyed one reads its clock, whose state row the manifest records with the timeline's.
/// </para>
/// </summary>
public sealed class WorldPresentationManifest {
    /// <summary>The manifest of a document that binds nothing.</summary>
    public static readonly WorldPresentationManifest Empty = new(
        bindings: [],
        bodyBindings: [],
        clocks: [],
        templates: []
    );

    private static readonly ConditionalWeakTable<WorldDefinition, WorldPresentationManifest> Compiled = new();
    private static readonly WorldDocumentWalker Walker = new(surfaces: [
        typeof(BindableAngle),
        typeof(BindableColor),
        typeof(BindableScalar),
        typeof(CreationDriverDocument),
        typeof(DocumentScalar),
        typeof(CreationEffectorDocument),
        typeof(CreationEffectorTargetDocument),
        typeof(OverlayPredicate.State),
        typeof(PaletteEntryDocument),
        typeof(PaletteRadialStopDocument),
        typeof(PaletteSurfaceDocument),
        typeof(WorldBindingBarAuthoring),
        typeof(WorldHudElement),
        typeof(WorldLookMotion),
        typeof(WorldScreenSource.Text),
    ]);

    private readonly WorldPresentationBinding[] m_bindings;
    private readonly WorldPresentationBinding[] m_bodyBindings;
    private readonly WorldClock[] m_clocks;
    private readonly Lazy<HashSet<(string Path, StateBinding Binding)>> m_authored;
    private readonly Dictionary<object, WorldPresentationBinding[]> m_templates;

    private WorldPresentationManifest(WorldPresentationBinding[] bindings, WorldPresentationBinding[] bodyBindings, WorldClock[] clocks, Dictionary<object, WorldPresentationBinding[]> templates, WorldDefinition? definition = null) {
        m_authored = new Lazy<HashSet<(string Path, StateBinding Binding)>>(valueFactory: () => ((definition is null)
            ? []
            : [.. WorldKeyedValues.BoundOf(definition: definition).Where(predicate: static bound => (bound.Field is not null)).Select(selector: static bound => (bound.Path, bound.Binding))]));
        m_bindings = bindings;
        m_bodyBindings = bodyBindings;
        m_clocks = clocks;
        m_templates = templates;
        SimulationRateHz = (definition?.SimulationRateHz ?? WorldDefinition.UnauthoredSimulationRateHz);
    }

    /// <summary>Gets the authored simulation rate used to map a clock preview's engine tick to a row read.</summary>
    public int SimulationRateHz { get; }

    /// <summary>Returns whether the document authors a presentation scalar at a path bound to a state binding: the
    /// identity of a bound field, which lasts exactly as long as the document keeps authoring it.</summary>
    /// <param name="path">The document path of the field, such as <c>render.sky.layers[0].softness</c>.</param>
    /// <param name="binding">The state binding the field is bound to.</param>
    /// <returns><see langword="true"/> when the document binds that field to that binding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public bool Authors(string path, in StateBinding binding) {
        ArgumentNullException.ThrowIfNull(argument: path);

        return m_authored.Value.Contains(item: (path, binding));
    }

    /// <summary>Gets the reads that last as long as the document, each once, in document order.</summary>
    public ReadOnlySpan<WorldPresentationBinding> Bindings => m_bindings;
    /// <summary>Gets the templates a body reads through its lease, each once, in document order; a template's key may
    /// be <see cref="StateBinding.BodyKey"/>.</summary>
    public ReadOnlySpan<WorldPresentationBinding> BodyBindings => m_bodyBindings;
    /// <summary>Gets the document's presentation clocks (its <c>timeline</c> section), which every keyed value reads
    /// by name; a state clock's row is among <see cref="Bindings"/>, read eased as a number.</summary>
    public ReadOnlySpan<WorldClock> Clocks => m_clocks;

    /// <summary>Returns the clock the document's timeline names.</summary>
    /// <param name="name">The clock's name.</param>
    /// <param name="clock">The clock, or <see langword="null"/> when the timeline names none.</param>
    /// <returns><see langword="true"/> when the timeline names the clock.</returns>
    public bool TryClock(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldClock? clock) {
        foreach (var candidate in m_clocks) {
            if (string.Equals(
                a: candidate.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                clock = candidate;

                return true;
            }
        }

        clock = null;

        return false;
    }
    /// <summary>Returns the binding a state clock's row is read through: the row's slot cell, eased.</summary>
    /// <param name="clock">The state clock.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentException"><paramref name="clock"/> is a tick clock.</exception>
    public static StateBinding ClockBinding(WorldClock clock) {
        ArgumentNullException.ThrowIfNull(argument: clock);

        return new StateBinding(
            Key: null,
            Row: (clock.State ?? throw new ArgumentException(
                message: $"Clock '{clock.Name}' reads the tick, not a state row.",
                paramName: nameof(clock)
            )),
            Target: false
        );
    }
    /// <summary>Returns the templates one document object carries, each once, in document order: the document's own
    /// (<see cref="WorldDefinition"/>, the population's scale row every body reads), a <see cref="WorldLook"/>'s pose
    /// references and lane operands, or a <see cref="WorldPrototype"/>'s driver and effector reads. The object is found by reference, so
    /// it must be the instance the compiled document holds.</summary>
    /// <param name="owner">The document object.</param>
    /// <returns>The templates; empty for an object that carries none or that the document does not hold.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    public ReadOnlySpan<WorldPresentationBinding> TemplatesOf(object owner) {
        ArgumentNullException.ThrowIfNull(argument: owner);

        return (m_templates.TryGetValue(
            key: owner,
            value: out var templates
        )
            ? templates
            : []
        );
    }
    /// <summary>Returns a document's manifest, compiling it on the first request for that document instance and
    /// answering later requests for the same instance from a table that does not keep the document alive.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The document's manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldPresentationManifest Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Compiled.GetValue(
            createValueCallback: static document => Compile(definition: document),
            key: definition
        );
    }
    /// <summary>Compiles a document's manifest: the deduplicated union of the state bindings its presentation
    /// sections author.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldPresentationManifest Compile(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var builder = new Builder { Definition = definition };

        if (definition.Population.Scale is { } scale) {
            builder.Owner = definition;
            builder.AddBody(binding: new StateBinding(
                Key: StateBinding.BodyKey,
                Row: WorldBodyStateReads.BindingRow(slot: scale),
                Target: false
            ));
            builder.Owner = null;
        }

        Walker.Walk(
            root: definition,
            visitor: builder
        );
        foreach (var region in definition.FieldRegions) { builder.AddFields(fields: region); }

        var clocks = (definition.Timeline.Clocks ?? []).Where(predicate: static clock => (clock is not null)).ToArray();

        foreach (var clock in clocks) {
            if (clock.IsStateClock) {
                builder.Add(
                    binding: ClockBinding(clock: clock),
                    conversion: WorldStateConversion.Number
                );
            }
        }

        return (((builder.Bindings.Count == 0) && (builder.BodyBindings.Count == 0) && (clocks.Length == 0))
            ? Empty
            : new WorldPresentationManifest(
                bindings: [.. builder.Bindings],
                bodyBindings: [.. builder.BodyBindings],
                clocks: clocks,
                templates: builder.Templates.ToDictionary(
                    comparer: ReferenceEqualityComparer.Instance,
                    elementSelector: static pair => pair.Value.ToArray(),
                    keySelector: static pair => pair.Key
                ),
                definition: definition
            )
        );
    }
    /// <summary>Compiles the reads one seat's presentation makes that no single document records, because the seat
    /// composes their sources: its binding document, composed from the world's overlays, the seat identity's layer and
    /// its session rebinds, and its binding bar, resolved from the identity or the world
    /// (<see cref="WorldBindingBarAuthoring.Resolve"/>). They are every state-backed binding context family's row
    /// (<c>state:&lt;row&gt;</c>, read as stored truth and keyed by the seat's body when the row is keyed), every radial
    /// wheel's label and icon cells keyed by each sector's id and the label row's
    /// <see cref="BindingWheelDefinition.HubLabelKey"/>, the bar's icon cell keyed by every page entry's
    /// <see cref="BindingPageEntryDefinition.KeyOf"/>, the bar's layout and model cells, and every binding and template
    /// placeholder of the seat's own player-scope HUD panel. A seat registers them on the mirror it reads through
    /// (<see cref="WorldStateMirror.Register"/>), so none is first read on a frame.</summary>
    /// <param name="definition">The world the seat presents from, whose rows say which context family is keyed.</param>
    /// <param name="bindings">The seat's composed binding document.</param>
    /// <param name="bar">The seat's resolved binding bar.</param>
    /// <param name="bodyIndex">The seat's controlled body, or -1 for none; a keyed family reads nothing without one.</param>
    /// <param name="hud">The seat's player-scope HUD panel, its profile's <c>Hud</c>, or <see langword="null"/> for
    /// none.</param>
    /// <returns>The reads, each once, in document order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/>, <paramref name="bindings"/> or
    /// <paramref name="bar"/> is <see langword="null"/>.</exception>
    public static WorldPresentationBinding[] SeatBindings(WorldDefinition definition, BindingProfileDocument bindings, WorldBindingBarAuthoring bar, int bodyIndex, WorldHudPanel? hud) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: bar);

        var builder = new Builder();

        foreach (var context in (bindings.Contexts ?? [])) {
            if (
                (context is null) ||
                !WorldStateBindingContext.TryResolveRow(
                definition: definition,
                family: context.Family,
                row: out var row
            ) ||
                !StateBinding.TryResolveBodyKey(
                bodyIndex: bodyIndex,
                key: (row.IsKeyed
                ? StateBinding.BodyKey
                : null),
                resolved: out var key
            )
            ) {
                continue;
            }

            builder.Add(
                binding: new StateBinding(
                    Key: key,
                    Row: row.Name.Value,
                    Target: true
                ),
                conversion: WorldStateConversion.Number
            );
        }
        foreach (var wheel in (bindings.Wheels ?? [])) {
            if (wheel is null) {
                continue;
            }

            builder.AddCell(
                key: BindingWheelDefinition.HubLabelKey,
                rowReference: wheel.LabelRow
            );

            foreach (var ring in wheel.Rings) {
                foreach (var sector in (ring?.Entries ?? [])) {
                    builder.AddCell(
                        key: sector?.Id,
                        rowReference: wheel.LabelRow
                    );
                    builder.AddCell(
                        key: sector?.Id,
                        rowReference: wheel.IconRow
                    );
                }
            }
        }
        // A building seat swaps its bar for the editor's (WorldBindingBarControl), so the editor bar's cells are read too.
        AddBar(bar: bar);

        if ((definition.EditorRaw?.Bar is { } editorBar) && !ReferenceEquals(objA: editorBar, objB: bar)) {
            AddBar(bar: editorBar);
        }

        void AddBar(WorldBindingBarAuthoring bar) {
            foreach (var chord in bindings.Chords) {
                // A page's own id keys the icon a plate wears when its control opens that page (BindingPageView.Openers).
                builder.AddCell(
                    key: chord?.Page?.Id,
                    rowReference: bar.IconRow
                );

                foreach (var entry in (chord?.Page?.Entries ?? [])) {
                    if (entry is not null) {
                        builder.AddCell(
                            key: BindingPageEntryDefinition.KeyOf(entry: entry),
                            rowReference: bar.IconRow
                        );
                    }
                }
            }

            builder.Add(
                binding: StateBinding.Parse(token: bar.LayoutCell),
                conversion: WorldStateConversion.Number
            );
            builder.Add(
                binding: StateBinding.Parse(token: bar.ModelCell),
                conversion: WorldStateConversion.Number
            );
        }

        foreach (var element in (hud?.Elements ?? [])) {
            if (element is not null) {
                builder.AddHud(element: element);
            }
        }

        return [.. builder.Bindings];
    }

    private sealed class Builder : IWorldDocumentVisitor {
        private readonly HashSet<WorldPresentationBinding> m_bodySeen = [];
        private readonly Stack<object?> m_owners = [];
        private readonly HashSet<WorldPresentationBinding> m_seen = [];

        public List<WorldPresentationBinding> Bindings { get; } = [];

        // The document the walk compiles, which says whether a keyless token names a keyed row; null for a seat's reads.
        public WorldDefinition? Definition { get; init; }

        public List<WorldPresentationBinding> BodyBindings { get; } = [];

        // The look or creation whose members the walk is inside, which each template is recorded under.
        public object? Owner { get; set; }

        public Dictionary<object, List<WorldPresentationBinding>> Templates { get; } = new(comparer: ReferenceEqualityComparer.Instance);

        public void Add(StateBinding? binding, WorldStateConversion conversion) {
            if (
                (binding is { } bound) &&
                m_seen.Add(item: new WorldPresentationBinding(
                    Binding: bound,
                    Conversion: conversion
                ))
            ) {
                Bindings.Add(item: new WorldPresentationBinding(
                    Binding: bound,
                    Conversion: conversion
                ));
            }
        }
        // A HUD element's binding and every placeholder of its template, each read as a number.
        public void AddHud(WorldHudElement element) {
            Add(
                binding: StateBinding.Parse(token: element.Binding),
                conversion: WorldStateConversion.Number
            );

            if (
                (element.Template is { Length: > 0 } template) &&
                HudTemplate.TryEnumeratePlaceholders(
                error: out _,
                placeholders: out var placeholders,
                template: template
            )
            ) {
                foreach (var placeholder in placeholders) {
                    Add(
                        binding: StateBinding.Parse(token: placeholder),
                        conversion: WorldStateConversion.Number
                    );
                }
            }
        }
        // A color a signed-distance program bakes at build (a palette's, a field's, a text screen's ink), authored as a
        // #RRGGBB literal or a state binding naming a Text cell; a literal reads nothing.
        public void AddColor(string? token) => Add(
            binding: StateBinding.Parse(token: token),
            conversion: WorldStateConversion.Color
        );
        // What the field lattice's presentation reads: each height field's row whole, which its brick is baked from,
        // and its color. The field rows live in the state section, whose rows the walk never reads.
        public void AddFields(WorldFieldsSection? fields) {
            foreach (var row in (fields?.Fields ?? [])) {
                if (row.HeightScale <= 0f) {
                    continue;
                }

                Add(
                    binding: new StateBinding(
                        Key: null,
                        Row: row.Name,
                        Target: false
                    ),
                    conversion: WorldStateConversion.Row
                );
                AddColor(token: row.Color);
            }
        }
        // A keyed cell of a row named by a state.<row> reference, read as text through its number slot.
        public void AddCell(string? rowReference, string? key) {
            if (
                (key is { Length: > 0 }) &&
                WorldStateBindingContext.TryParseRowReference(
                reference: rowReference,
                rowName: out var row
            )
            ) {
                Add(
                    binding: new StateBinding(
                        Key: key,
                        Row: row,
                        Target: false
                    ),
                    conversion: WorldStateConversion.Number
                );
            }
        }
        // A lease reads every template as a number.
        public void AddBody(StateBinding binding) {
            var entry = new WorldPresentationBinding(
                Binding: binding,
                Conversion: WorldStateConversion.Number
            );

            if (m_bodySeen.Add(item: entry)) {
                BodyBindings.Add(item: entry);
            }
            if (Owner is not { } owner) {
                return;
            }
            if (!Templates.TryGetValue(
                key: owner,
                value: out var templates
            )) {
                templates = [];
                Templates[owner] = templates;
            }
            if (!templates.Contains(item: entry)) {
                templates.Add(item: entry);
            }
        }
        // A lease reads a token with the truth when its reader asks for it, and whenever the token spells .$target.
        public void AddBodyToken(string? token, bool truth) {
            if (StateBinding.TryParse(
                binding: out var binding,
                token: token
            )) {
                AddBody(binding: (binding with { Target = (truth || binding.Target) }));
            }
        }
        // A driver's or an effector's gate reads each state token's truth (WorldGaitDrivers.HoldsGate).
        public void AddGate(IReadOnlyList<string>? gate) {
            foreach (var token in (gate ?? [])) {
                if (CreationDriverDocument.IsStateSignal(signal: token)) {
                    AddBodyToken(
                        token: token,
                        truth: true
                    );
                }
            }
        }
        // A look's or a creation's members record their templates under it.
        public bool Enter(object value, WorldDocumentSite site) {
            if (!Collect(value: value)) {
                return false;
            }
            if (value is WorldLook or WorldPrototype) {
                m_owners.Push(item: Owner);
                Owner = value;
            }

            return true;
        }
        public void Leave(object value) {
            if (value is WorldLook or WorldPrototype) {
                Owner = m_owners.Pop();
            }
        }

        // Records the bindings a surface carries; returns whether the walk continues into its members.
        private bool Collect(object value) {
            switch (value) {
                case DocumentScalar scalar:
                    if (Owner is not null) {
                        AddBodyToken(token: scalar.Reference, truth: false);
                    } else if (StateBinding.TryParse(binding: out var binding, token: scalar.Reference)) {
                        Add(binding: binding, conversion: WorldStateConversion.Number);
                    }

                    return false;
                case BindableScalar scalar:
                    // A token naming a keyed row with no key binds the whole row, which a pass's array reads.
                    Add(
                        binding: scalar.State,
                        conversion: (((scalar.State is { Key: null } whole) && (Definition is { } document) && WorldBoundRow.TryResolve(
                            definition: document,
                            length: out _,
                            row: out _,
                            rowName: whole.Row
                        ))
                            ? WorldStateConversion.Row
                            : WorldStateConversion.Number)
                    );

                    return false;
                case BindableAngle angle:
                    Add(
                        binding: angle.Value.State,
                        conversion: WorldStateConversion.Number
                    );

                    return false;
                case BindableColor color:
                    Add(
                        binding: color.State,
                        conversion: WorldStateConversion.Color
                    );

                    return false;
                case PaletteEntryDocument entry:
                    // The walk continues into the entry's weathering and inset surfaces.
                    AddColor(token: entry.Color);
                    AddColor(token: entry.Fill);
                    AddColor(token: entry.Bleed);
                    if (entry.ToonSelect is { } select) {
                        Add(
                            binding: StateBinding.Parse(token: select.State),
                            conversion: WorldStateConversion.Number
                        );
                    }

                    return true;
                case PaletteRadialStopDocument stop:
                    AddColor(token: stop.Color);

                    return false;
                case PaletteSurfaceDocument surface:
                    AddColor(token: surface.Color);

                    return false;
                case WorldScreenSource.Text text:
                    AddColor(token: text.Foreground);
                    AddColor(token: text.Background);

                    foreach (var (_, _, binding) in WorldScreenText.Placeholders(text: text)) {
                        Add(
                            binding: binding,
                            conversion: WorldStateConversion.Number
                        );
                    }

                    return false;
                case OverlayPredicate.State state:
                    Add(
                        binding: StateBinding.Parse(token: state.Binding),
                        conversion: WorldStateConversion.Number
                    );

                    return false;
                case WorldHudElement element:
                    AddHud(element: element);

                    return true;
                case WorldBindingBarAuthoring bar:
                    Add(
                        binding: StateBinding.Parse(token: bar.LayoutCell),
                        conversion: WorldStateConversion.Number
                    );
                    Add(
                        binding: StateBinding.Parse(token: bar.ModelCell),
                        conversion: WorldStateConversion.Number
                    );

                    return true;
                case WorldLookMotion motion:
                    if (motion.Poses is { } poses) {
                        foreach (var reference in poses.Values) {
                            AddBodyToken(
                                token: reference,
                                truth: true
                            );
                        }
                    }
                    if (motion.Lanes is { } lanes) {
                        foreach (var lane in lanes) {
                            if (lane is null) {
                                continue;
                            }

                            foreach (var instruction in lane.Instructions) {
                                if (instruction.Payload is InstructionPayload.State operand) {
                                    AddBody(binding: WorldLookLaneReads.Binding(operand: operand));
                                }
                            }
                        }
                    }

                    return true;
                case CreationDriverDocument driver:
                    if (CreationDriverDocument.IsStateSignal(signal: driver.Signal)) {
                        AddBodyToken(
                            token: driver.Signal,
                            truth: false
                        );
                    }

                    AddGate(gate: driver.When);

                    // Scalar expressions are surfaces too, wherever a driver carries them.
                    return true;
                case CreationEffectorDocument effector:
                    // The walk continues into the effector's target.
                    AddGate(gate: effector.When);

                    return true;
                case CreationEffectorTargetDocument target:
                    if (string.Equals(
                        a: target.Kind,
                        b: CreationEffectorTargetDocument.KindState,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        AddBodyToken(
                            token: target.Reference,
                            truth: false
                        );
                    }

                    return false;
                default:
                    return true;
            }
        }
    }
}
