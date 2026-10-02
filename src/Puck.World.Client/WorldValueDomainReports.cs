using System.Globalization;

namespace Puck.World.Client;

/// <summary>Where a resolved field sits in the document, for a report that names it: the section, the row's index in
/// it when the section is a list, and the member path between the row and the field.</summary>
/// <param name="Section">The section's path, such as <c>render.sky.layers</c>.</param>
/// <param name="Index">The row's index in the section, or -1 when the section is no list.</param>
/// <param name="Inner">The member path from the row to the field's owner, such as <c>twinkle</c>, or
/// <see langword="null"/> when the row owns the field.</param>
public readonly record struct WorldValueSite(string Section, int Index = -1, string? Inner = null) {
    /// <summary>Returns the document path of a field at this site.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The path, such as <c>render.sky.layers[3].softness</c>.</returns>
    public string PathOf(WorldValueField field) {
        ArgumentNullException.ThrowIfNull(argument: field);

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{Section}{((Index >= 0) ? $"[{Index}]" : string.Empty)}.{((Inner is null) ? string.Empty : $"{Inner}.")}{field.Name}"
        );
    }
}
/// <summary>
/// Maps a resolved presentation scalar into its field's declared domain (<see cref="WorldValueFields"/>) and reports
/// the first value each bound row presents outside it. The value presented is <see cref="WorldValueDomain.Clamp"/> of
/// the value the state mirror resolves, a pure function of the current state, so a seek, a replay or a capture that
/// rebuilds presentation from state presents the same value; only the report remembers anything. A field at one site
/// bound to one row is reported once, however often or however far its row strays.
/// </summary>
public sealed class WorldValueDomainReports {
    private readonly HashSet<(WorldValueField Field, WorldValueSite Site, string Row)> m_reported = [];

    /// <summary>Gets or sets where a report goes: the host's diagnostic fan-out, or <see langword="null"/> to count
    /// reports without delivering them.</summary>
    public Action<string>? Report { get; set; }
    /// <summary>Gets how many reports have been made.</summary>
    public int Reported => m_reported.Count;

    /// <summary>Returns a resolved value mapped into its field's domain, reporting a bound value that lay outside it
    /// the first time its field, site and row present one.</summary>
    /// <param name="domains">The reports, or <see langword="null"/> to map without reporting.</param>
    /// <param name="field">The field the value resolves.</param>
    /// <param name="scalar">The authored scalar the value resolved from.</param>
    /// <param name="value">The value the state mirror resolved.</param>
    /// <param name="site">Where the field sits, which a report names.</param>
    /// <returns>The value, mapped into the field's domain.</returns>
    public static float Clamp(WorldValueDomainReports? domains, WorldValueField field, in BindableScalar scalar, float value, in WorldValueSite site) {
        ArgumentNullException.ThrowIfNull(argument: field);

        var used = field.Domain.Clamp(value: value);

        if (
            (domains is not null) &&
            !used.Equals(obj: value) &&
            (scalar.State is { } binding) &&
            domains.m_reported.Add(item: (field, site, binding.Row))
        ) {
            domains.Report?.Invoke(obj: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"[world.value: {site.PathOf(field: field)} reads {value} from {scalar}, outside {field.Domain}; presenting {used}]"
            ));
        }

        return used;
    }
}
