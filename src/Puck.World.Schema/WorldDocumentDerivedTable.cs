using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions;

namespace Puck.World;

/// <summary>
/// A weak table of values derived from a document, each held against the <see cref="WorldDefinition"/> it was derived
/// from (a <see cref="CodeDerivedTable{TKey, TValue}"/>), together with the parts of the document its derivation read
/// (<see cref="WorldDocumentReads"/>). Every lookup that answers a value, held or newly derived, notes those parts, so
/// a product build that reads the value records the same parts whether the table already held it or not: what a
/// product was built from (<see cref="WorldDocumentInputs"/>) is a function of the document, never of which caller
/// asked first. A raw <see cref="CodeDerivedTable{TKey, TValue}"/> keyed by a definition hides its derivation's reads
/// from every build that finds the value held.
/// </summary>
/// <typeparam name="TValue">The type of the derived values.</typeparam>
public sealed class WorldDocumentDerivedTable<TValue> where TValue : class? {
    private sealed class Entry(TValue value, WorldDocumentInputs inputs) {
        public TValue Value { get; } = value;
        public WorldDocumentInputs Inputs { get; } = inputs;
    }

    private readonly CodeDerivedTable<WorldDefinition, Entry> m_entries = new();

    /// <summary>Gets the value derived from <paramref name="definition"/>, deriving and holding it when the table holds
    /// none under the running code, and notes the parts of the document the derivation read.</summary>
    /// <param name="definition">The document the value is derived from.</param>
    /// <param name="derive">The derivation, which reads the document on the calling thread; called once per document
    /// and code revision unless two callers race, when the first value held wins.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="derive"/> is
    /// <see langword="null"/>.</exception>
    public TValue GetValue(WorldDefinition definition, Func<WorldDefinition, TValue> derive) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: derive);

        if (m_entries.TryGetValue(key: definition, value: out var held)) {
            WorldDocumentReads.Note(inputs: held.Inputs);
            return held.Value;
        }

        // Unverified: a definition property the read table's own check reads may derive through a table.
        var recording = WorldDocumentReads.BeginUnverified();
        TValue value;
        WorldDocumentInputs inputs;

        try {
            value = derive(arg: definition);
        } finally {
            inputs = recording.End();
        }
        var derived = new Entry(inputs: inputs, value: value);
        // Static delegates: a capturing lambda here would allocate its closure on every call, the held path's included.
        var entry = m_entries.GetValue(definition, derived, static (_, candidate) => candidate, static (_, _, _) => true);

        // A racing caller's value won: its derivation read the same document, and its parts are the ones it holds.
        if (!ReferenceEquals(objA: entry, objB: derived)) { WorldDocumentReads.Note(inputs: entry.Inputs); }
        return entry.Value;
    }
    /// <summary>Reads the value held for <paramref name="definition"/> under the running code, noting the parts of the
    /// document its derivation read when one is held.</summary>
    /// <param name="definition">The document the value is derived from.</param>
    /// <param name="value">The held value, or the default when none is held.</param>
    /// <returns><see langword="true"/> when a value is held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public bool TryGetValue(WorldDefinition definition, [MaybeNullWhen(returnValue: false)] out TValue value) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (m_entries.TryGetValue(key: definition, value: out var held)) {
            WorldDocumentReads.Note(inputs: held.Inputs);
            value = held.Value;
            return true;
        }
        value = default;
        return false;
    }
    /// <summary>Returns whether the table holds a value for <paramref name="definition"/> under the running code,
    /// without reading it or noting anything.</summary>
    /// <param name="definition">The document.</param>
    /// <returns><see langword="true"/> when a value is held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public bool Holds(WorldDefinition definition) => m_entries.TryGetValue(key: definition, value: out _);
    /// <summary>Holds the value derived from <paramref name="source"/> for <paramref name="target"/> as well, with the
    /// parts its derivation read, when the table holds one: for a target that differs from the source in no part the
    /// derivation reads. Notes nothing, since no product reads the value here.</summary>
    /// <param name="source">The document the value was derived from.</param>
    /// <param name="target">The document that derives the same value.</param>
    /// <returns><see langword="true"/> when the source held a value and the target now holds it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="target"/> is
    /// <see langword="null"/>.</exception>
    public bool Carry(WorldDefinition source, WorldDefinition target) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: target);

        if (!m_entries.TryGetValue(key: source, value: out var held)) { return false; }
        m_entries.AddOrUpdate(key: target, value: held);
        return true;
    }
}
