namespace Puck.Text;

/// <summary>A raw name/value pair parsed from an enrichment tag, before it is folded onto a <see cref="TextEffect"/>.</summary>
/// <param name="Name">The parameter name (e.g. <c>amplitude</c>, <c>color</c>).</param>
/// <param name="Value">The parameter's raw value string (may carry a binding sigil).</param>
public readonly record struct TextEnrichmentTagParameter(string Name, string Value);
