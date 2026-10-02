using System.Globalization;
using System.Runtime.CompilerServices;

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
/// Maps what a bound row presents into its field's declared domain (<see cref="WorldValueFields"/>), by one rule. A
/// value inside the domain is presented as it is. A finite value beyond a closed end is clamped to it. A value that is
/// not finite, or lies at or beyond an open end, cannot be clamped to anything admissible, so the binding holds the last
/// value it presented from a valid one (the field's fallback before it has presented any). The guard reports each
/// transition of a binding, once when it leaves its domain and once when it returns, never once per frame.
/// <para>
/// The last valid value is state, owned by the binding instance in its world: one entry per field, site and state
/// binding of one <see cref="WorldStateMirror"/>. An entry is released when its binding stops being registered in the
/// mirror (a document that no longer binds the row) and with the mirror itself, so what the guard keeps stays bounded by
/// the live documents however many bindings come and go. An input that has not changed since the entry last saw it
/// returns the value presented then and does no counted work (<see cref="Checks"/>).
/// </para>
/// <para>
/// Only a binding to a state row can leave its domain after the document loads: a literal and a key are judged at load,
/// and a key blends inside the interval its keys span. A coupled threshold (the sky's gradient stops, the curvature ink
/// band) must stay ordered, which no row can promise, so the validator refuses to bind either end to a row and the guard
/// never sees one end alone.
/// </para>
/// </summary>
public sealed class WorldValueDomainGuard {
    private readonly ConditionalWeakTable<WorldStateMirror, World> m_worlds = [];

    private long m_checks;
    private long m_holds;
    private long m_reported;

    /// <summary>Gets or sets where a report goes: the host's diagnostic fan-out, or <see langword="null"/> to count
    /// reports without delivering them.</summary>
    public Action<string>? Report { get; set; }
    /// <summary>Gets how many changed inputs the guard has checked against a domain. An input unchanged since its binding
    /// last saw it is not checked.</summary>
    public long Checks => Interlocked.Read(location: ref m_checks);
    /// <summary>Gets how many checked inputs held their binding's last valid value.</summary>
    public long Holds => Interlocked.Read(location: ref m_holds);
    /// <summary>Gets how many transitions have been reported: a binding leaving its domain, and one returning.</summary>
    public long Reported => Interlocked.Read(location: ref m_reported);
    /// <summary>Gets how many binding instances the guard holds state for, across every world it has seen.</summary>
    public int Tracked {
        get {
            var tracked = 0;

            foreach (var (_, world) in m_worlds) {
                lock (world) {
                    tracked += world.Entries.Count;
                }
            }

            return tracked;
        }
    }

    /// <summary>Returns a resolved value mapped into its field's domain.</summary>
    /// <param name="domains">The guard that holds the last valid value of a bound field and reports its transitions, or
    /// <see langword="null"/> to map without either: a value that cannot be clamped then presents
    /// <paramref name="fallback"/>.</param>
    /// <param name="mirror">The state mirror the value was resolved through, which names the world the binding lives
    /// in.</param>
    /// <param name="field">The field the value resolves.</param>
    /// <param name="scalar">The authored scalar the value resolved from.</param>
    /// <param name="value">The value the state mirror resolved.</param>
    /// <param name="fallback">The field's value before its binding has presented one from a valid input, the engine
    /// default.</param>
    /// <param name="site">Where the field sits, which a report names.</param>
    /// <returns>The value, mapped into the field's domain.</returns>
    public static float Resolve(WorldValueDomainGuard? domains, WorldStateMirror mirror, WorldValueField field, in BindableScalar scalar, float value, float fallback, in WorldValueSite site) {
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: field);

        if ((domains is null) || (scalar.State is not { } binding)) {
            var mapped = field.Domain.Map(value: value);

            return (mapped.Holds
                ? fallback
                : mapped.Value);
        }

        return domains.Check(
            binding: in binding,
            fallback: fallback,
            field: field,
            mirror: mirror,
            scalar: in scalar,
            site: in site,
            value: value
        );
    }

    private float Check(in StateBinding binding, float fallback, WorldValueField field, WorldStateMirror mirror, in BindableScalar scalar, in WorldValueSite site, float value) {
        var world = m_worlds.GetValue(
            key: mirror,
            createValueCallback: static candidate => new World(generation: candidate.Generation)
        );
        string? report = null;
        float used;

        lock (world) {
            if (world.Generation != mirror.Generation) {
                world.Release(mirror: mirror);
            }

            var key = new Key(
                Binding: binding,
                Field: field,
                Site: site
            );

            if (
                !world.Entries.TryGetValue(
                key: key,
                value: out var entry
            )
            ) {
                entry = new Entry(binding: binding);
                world.Entries.Add(
                    key: key,
                    value: entry
                );
            } else if (entry.Raw.Equals(obj: value)) {
                return entry.Used;
            }

            _ = Interlocked.Increment(location: ref m_checks);

            var domain = field.Domain;
            var mapped = domain.Map(value: value);
            var holds = mapped.Holds;
            var invalid = !domain.Contains(value: value);

            if (holds) {
                _ = Interlocked.Increment(location: ref m_holds);
                used = (entry.HasValid
                    ? entry.Valid
                    : fallback);
            } else {
                used = mapped.Value;
                entry.Valid = used;
                entry.HasValid = true;
            }

            entry.Raw = value;
            entry.Used = used;

            if (invalid != entry.Invalid) {
                entry.Invalid = invalid;
                report = (invalid
                    ? string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"[world.value: {site.PathOf(field: field)} reads {value} from {scalar}, outside {domain}; {(holds ? "holding" : "presenting")} {used}]"
                    )
                    : string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"[world.value: {site.PathOf(field: field)} reads {value} from {scalar}, within {domain}; recovered]"
                    ));
            }
        }

        if (report is not null) {
            _ = Interlocked.Increment(location: ref m_reported);
            Report?.Invoke(obj: report);
        }

        return used;
    }

    private readonly record struct Key(StateBinding Binding, WorldValueField Field, WorldValueSite Site) : IEquatable<Key> {
        // The field is one of WorldValueFields' rows, so it is compared by reference rather than member by member.
        public bool Equals(Key other) => (
            ReferenceEquals(
                objA: Field,
                objB: other.Field
            ) &&
            Site.Equals(other: other.Site) &&
            Binding.Equals(other: other.Binding)
        );
        public override int GetHashCode() => HashCode.Combine(
            value1: RuntimeHelpers.GetHashCode(o: Field),
            value2: Site,
            value3: Binding
        );
    }
    private sealed class Entry(StateBinding binding) {
        public StateBinding Binding { get; } = binding;
        public bool HasValid { get; set; }
        public bool Invalid { get; set; }
        public float Raw { get; set; }
        public float Used { get; set; }
        public float Valid { get; set; }
    }
    // One world's entries, which the guard drops with the world's mirror.
    private sealed class World(int generation) {
        public Dictionary<Key, Entry> Entries { get; } = [];
        public int Generation { get; private set; } = generation;

        // Lets go of the entries whose binding the mirror no longer registers: a document that replaced or removed the
        // binding retired its slot, and the entry is that binding's last valid value, which the binding no longer has.
        public void Release(WorldStateMirror mirror) {
            Generation = mirror.Generation;

            foreach (var (key, entry) in Entries.ToArray()) {
                var binding = entry.Binding;

                if (mirror.SlotOf(
                    binding: in binding,
                    conversion: WorldStateConversion.Number
                ) < 0) {
                    _ = Entries.Remove(key: key);
                }
            }
        }
    }
}
