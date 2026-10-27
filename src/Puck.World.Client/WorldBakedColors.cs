using System.Numerics;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The colors a signed-distance program or a screen decal bakes when it is built — a creation palette's surface,
/// fill, bleed, weathering and inset colors, a height field's color, a text screen's ink — read through the state mirror,
/// the one path presentation reads state through. A <c>#RRGGBB</c> literal parses directly; a state binding reads the
/// color slot the document's presentation manifest registered for it (<see cref="WorldPresentationManifest"/>), so a
/// bound color resolves from the mirror's sample rather than from a second read of the document.
/// <para>
/// A builder calls <see cref="Begin"/> when it starts a build, resolves every color through <see cref="Resolve"/>,
/// and asks <see cref="TryTakeMove"/> each frame whether a bound color it baked has moved since, which it answers once
/// per move. Bound colors are Text cells, which never ease, so a move is a write, and the builder rebuilds to follow
/// it. A check with no color slot moved compares two counters and allocates nothing.
/// </para>
/// </summary>
public sealed class WorldBakedColors {
    // The document a mirror is installed over on the first bound color, for colors read on their own (Of).
    private readonly WorldDefinition? m_definition;

    // One resolver a palette entry's layer colors read through, built once rather than per registered palette.
    private Func<string, Vector3>? m_layerColor;
    private int m_checkedColorRevision;
    private int m_checkedRevision;
    private int m_checkedGeneration;
    private int m_count;
    private bool m_hasValueBindings;
    private WorldStateMirror? m_mirror;
    private string[] m_tokens = [];
    private WorldStateConversion[] m_conversions = [];
    private int[] m_changed = [];

    /// <summary>Initializes a new instance of the <see cref="WorldBakedColors"/> class over a state mirror.</summary>
    /// <param name="mirror">The mirror every bound color reads through; its installed document's manifest registers the
    /// colors.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public WorldBakedColors(WorldStateMirror mirror) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        m_mirror = mirror;
    }

    private WorldBakedColors(WorldDefinition definition) => m_definition = definition;

    /// <summary>Gets whether a mirror backs these colors: always for colors over a client's mirror, and for a
    /// document's own colors (<see cref="Of"/>) only once a bound color has been resolved.</summary>
    public bool IsMirrored => (m_mirror is not null);

    /// <summary>Returns the colors of a document read on its own: what a tool, a law, or a build from a document with no
    /// client of its own resolves its colors through. A literal parses directly; the first bound color installs a
    /// mirror over the document, compiling its manifest and reading every slot once, so a build that bakes only
    /// literals builds no mirror.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The colors, read through a mirror of <paramref name="definition"/> once one is needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldBakedColors Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new WorldBakedColors(definition: definition);
    }

    /// <summary>Gets the resolver a palette entry's layer colors (weathering, inset) read through: <see cref="Resolve"/>
    /// with a black fallback, one delegate for the life of these colors.</summary>
    public Func<string, Vector3> LayerColor => (m_layerColor ??= value => Resolve(
        fallback: Vector3.Zero,
        value: value
    ));

    /// <summary>Starts a build: forgets the colors the previous build baked.</summary>
    public void Begin() {
        m_count = 0;
        m_hasValueBindings = false;
        m_checkedColorRevision = (m_mirror?.ColorRevision ?? 0);
        m_checkedRevision = (m_mirror?.Revision ?? 0);
        m_checkedGeneration = (m_mirror?.Generation ?? 0);
    }
    /// <summary>Resolves one authored color for the build under way and remembers a bound one.</summary>
    /// <param name="value">The authored color: a <c>#RRGGBB</c> literal, a <c>state.&lt;row&gt;[.&lt;key&gt;]</c>
    /// binding naming a Text cell, or <see langword="null"/>.</param>
    /// <param name="fallback">The color when <paramref name="value"/> is neither, the manifest registers no slot for the
    /// binding, or the cell holds no color.</param>
    /// <returns>The color, alpha dropped.</returns>
    public Vector3 Resolve(string? value, Vector3 fallback) {
        if (!StateBinding.TryParse(
            binding: out _,
            token: value
        )) {
            return HexColor.Parse(
                fallback: fallback,
                value: value
            );
        }

        var mirror = Mirror();
        var slot = mirror.SlotOf(
            conversion: WorldStateConversion.Color,
            token: value
        );

        Remember(
            changed: mirror.Changed(slot: slot),
            conversion: WorldStateConversion.Color,
            token: value!
        );

        return (mirror.TryColor(
            slot: slot,
            value: out var color
        )
            ? new Vector3(
                x: color.X,
                y: color.Y,
                z: color.Z
            )
            : fallback
        );
    }
    /// <summary>Resolves one authored text screen line for the build under way, replacing any
    /// <c>{state.&lt;row&gt;[.&lt;key&gt;]}</c> placeholders (<see cref="HudTemplate"/>) from the state mirror and
    /// remembering each bound cell.</summary>
    /// <param name="line">The authored line.</param>
    /// <returns>The resolved line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public string ResolveText(string line) {
        ArgumentNullException.ThrowIfNull(argument: line);

        if (
            !line.Contains(value: '{') &&
            !line.Contains(value: '}')
        ) {
            return line;
        }

        if (!WorldScreenText.TryParse(
            error: out _,
            line: line,
            segments: out var segments
        )) {
            return line;
        }

        var builder = new System.Text.StringBuilder(capacity: line.Length);

        foreach (var segment in segments) {
            if (!segment.IsPlaceholder) {
                builder.Append(value: segment.Text);

                continue;
            }

            if (!StateBinding.TryParse(
                binding: out _,
                token: segment.Text
            )) {
                continue;
            }

            var mirror = Mirror();
            var slot = mirror.SlotOf(
                conversion: WorldStateConversion.Number,
                token: segment.Text
            );

            m_hasValueBindings = true;
            Remember(
                changed: mirror.Changed(slot: slot),
                conversion: WorldStateConversion.Number,
                token: segment.Text
            );

            var value = mirror.Sample(slot: slot).Value;

            if (!value.HasValue) {
                continue;
            }

            switch (value.Kind) {
                case CellKind.Int:
                    builder.Append(value: value.AsInt.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));

                    break;
                case CellKind.Fixed:
                    builder.Append(value: Puck.Maths.FixedQ4816.FromRawBits(value: value.AsFixed).ToString());

                    break;
                case CellKind.Bool:
                    builder.Append(value: (value.AsBool
                        ? "true"
                        : "false"
                    ));

                    break;
                case CellKind.Text:
                    builder.Append(value: (value.AsText ?? string.Empty));

                    break;
            }
        }

        return builder.ToString();
    }
    /// <summary>Returns whether a bound color or text placeholder the last build baked has moved since it was resolved,
    /// or has lost or gained its slot, answering each move once.</summary>
    /// <returns><see langword="true"/> when the builder must rebuild to follow a bound value.</returns>
    public bool TryTakeMove() {
        if (
            (m_count == 0) ||
            (m_mirror is not { } mirror) ||
            ((m_hasValueBindings
                ? (mirror.Revision == m_checkedRevision)
                : (mirror.ColorRevision == m_checkedColorRevision)) &&
            (mirror.Generation == m_checkedGeneration))
        ) {
            return false;
        }

        m_checkedColorRevision = mirror.ColorRevision;
        m_checkedRevision = mirror.Revision;
        m_checkedGeneration = mirror.Generation;

        var moved = false;

        for (var index = 0; (index < m_count); index++) {
            var changed = mirror.Changed(slot: mirror.SlotOf(
                conversion: m_conversions[index],
                token: m_tokens[index]
            ));

            if (changed != m_changed[index]) {
                m_changed[index] = changed;
                moved = true;
            }
        }

        return moved;
    }

    // The mirror a bound color reads through, installed over the document on first need for colors read on their own.
    private WorldStateMirror Mirror() {
        if (m_mirror is { } mirror) {
            return mirror;
        }

        var definition = m_definition!;

        mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        m_mirror = mirror;
        m_checkedColorRevision = mirror.ColorRevision;
        m_checkedRevision = mirror.Revision;
        m_checkedGeneration = mirror.Generation;

        return mirror;
    }
    private void Remember(string token, WorldStateConversion conversion, int changed) {
        if (m_count == m_tokens.Length) {
            var capacity = Math.Max(
                val1: 8,
                val2: (m_tokens.Length * 2)
            );

            Array.Resize(
                array: ref m_tokens,
                newSize: capacity
            );
            Array.Resize(
                array: ref m_conversions,
                newSize: capacity
            );
            Array.Resize(
                array: ref m_changed,
                newSize: capacity
            );
        }

        m_tokens[m_count] = token;
        m_conversions[m_count] = conversion;
        m_changed[m_count] = changed;
        m_count++;
    }
}
