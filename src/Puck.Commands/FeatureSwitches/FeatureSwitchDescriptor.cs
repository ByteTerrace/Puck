namespace Puck.Commands;

/// <summary>
/// One toggleable engine capability, described as data: a dotted name, its classification, the values it accepts, and
/// the two delegates that read and write it. A descriptor carries <b>zero</b> engine references of its own — the
/// composition root builds it with <see cref="Get"/>/<see cref="Set"/> delegates that close over the object that
/// actually owns the lever (the neutral-seam pattern), so the registry and the <c>feature.*</c> verbs speak only the
/// switch vocabulary and never a backend, the SDF VM, or the demo.
/// </summary>
/// <param name="Name">The switch's unique, dotted name (for example <c>sdf.soft-shadows</c> or <c>render.scale</c>).</param>
/// <param name="Description">A human-readable one-line description shown in help/list output.</param>
/// <param name="Category">The switch's grouping — <c>render</c>, <c>gpu</c>, <c>presentation</c>, and so on.</param>
/// <param name="Kind">How the value reaches the engine (see <see cref="FeatureSwitchKind"/>).</param>
/// <param name="DefaultValue">The value the switch resets to; must appear in <paramref name="AllowedValues"/>.</param>
/// <param name="AllowedValues">The exact set of values the switch accepts — <c>["on", "off"]</c> for a flag, the tier
/// names for an <see cref="FeatureSwitchKind.EnumTier"/> switch.</param>
/// <param name="Get">Reads the switch's current value as one of <paramref name="AllowedValues"/>.</param>
/// <param name="Set">Applies a value and returns <see langword="true"/> when it took effect; <see langword="false"/>
/// means the value was <em>rejected</em> — applied nowhere — so a caller must not treat the switch as changed (a
/// read-only or boot-only switch rejects every live write this way).</param>
public sealed record FeatureSwitchDescriptor(
    string Name,
    string Description,
    string Category,
    FeatureSwitchKind Kind,
    string DefaultValue,
    IReadOnlyList<string> AllowedValues,
    Func<string> Get,
    Func<string, bool> Set
);
