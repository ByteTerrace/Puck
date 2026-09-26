namespace Puck.World.Client;

// A row's bound parameters (WorldViewGraph.Parameters): each scalar config field a row binds takes its value from the
// state mirror, the one presentation read of state, through the slot the presentation manifest registered for its token
// at install. A literal is written once; a state binding is read every frame and written only when its value moved, and
// a binding that does not resolve draws the field's source default, read back from the pass after the row's config is
// bound. Presentation reads state and never writes it.
public sealed partial class WorldViewGraphHost {
    /// <summary>Writes every row's bound parameters into its installed graph's passes from the state mirror, before the
    /// runtime schedules the frame. A value that has not moved since the last write is not written again, so a still
    /// frame writes nothing and allocates nothing.</summary>
    /// <param name="mirror">The state mirror the frame presents.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public void WriteParameters(WorldStateMirror mirror) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        foreach (var entry in m_entries.Values) {
            entry.WriteParameters(mirror: mirror);
        }
    }

    public sealed partial class Entry {
        private BoundParameter[] m_parameters = [];

        /// <summary>Gets how many of the row's bound parameters the installed graph accepted.</summary>
        public int BoundParameterCount => m_parameters.Length;

        // Whether a live preview may set a pass's field: a field the row binds is never overridden.
        internal bool Binds(string pass, string field) => (
            (Row?.Parameters is { } parameters) &&
            parameters.TryGetValue(
            key: pass,
            value: out var fields
        ) &&
            fields.ContainsKey(key: field)
        );

        // Resolves the row's parameters against the installed graph after its config is bound: each field's source
        // default is read back from the pass as the fallback, and every parameter is written again on the next frame,
        // since the bind replaced the pass's block.
        private void BindParameters() {
            if (Row?.Parameters is not { Count: > 0 } parameters) {
                m_parameters = [];

                return;
            }

            var bound = new List<BoundParameter>();

            foreach (var (pass, fields) in parameters.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
                foreach (var (field, value) in fields.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
                    var array = Node.DeclaresArray(
                        array: field,
                        passName: pass
                    );
                    var fallback = 0d;

                    if (
                        !array &&
                        !Node.TryReadParameter(
                        field: field,
                        passName: pass,
                        value: out fallback
                    )
                    ) {
                        Owner?.Report?.Invoke(
                            Name,
                            $"parameter refused: pass '{pass}' declares no scalar config field or array '{field}'"
                        );

                        continue;
                    }

                    bound.Add(item: new BoundParameter(
                        Array: array,
                        Fallback: fallback,
                        Field: field,
                        Pass: pass,
                        Value: value
                    ));
                }
            }

            m_parameters = [.. bound];
        }

        internal void WriteParameters(WorldStateMirror mirror) {
            foreach (var parameter in m_parameters) {
                if (parameter.Array) {
                    WriteArray(
                        mirror: mirror,
                        parameter: parameter
                    );

                    continue;
                }

                var value = parameter.Read(mirror: mirror);

                if (
                    parameter.Written &&
                    (value == parameter.Last)
                ) {
                    continue;
                }
                if (Node.TryWriteParameter(
                    field: parameter.Field,
                    passName: parameter.Pass,
                    value: value
                )) {
                    parameter.Last = value;
                    parameter.Written = true;
                }
            }
        }

        // An array takes its row slot's elements whenever the slot changed since the last write: a row read whole, never
        // a second read of the document. A binding that does not resolve writes zeros.
        private void WriteArray(WorldStateMirror mirror, BoundParameter parameter) {
            var slot = ((parameter.Value.State is { } binding)
                ? mirror.SlotOf(
                    binding: in binding,
                    conversion: WorldStateConversion.Row
                )
                : -1);
            var changed = mirror.Changed(slot: slot);

            if (
                parameter.Written &&
                (changed == parameter.Changed) &&
                (mirror.Generation == parameter.Generation)
            ) {
                return;
            }
            if (Node.TryWriteArray(
                array: parameter.Field,
                passName: parameter.Pass,
                values: mirror.RowValues(slot: slot)
            )) {
                parameter.Changed = changed;
                parameter.Generation = mirror.Generation;
                parameter.Written = true;
            }
        }
    }

    // One bound field or array and what was last written to it: a scalar's value, an array's slot revision and the
    // mirror generation it was read under.
    private sealed class BoundParameter(string Pass, string Field, BindableScalar Value, double Fallback, bool Array) {
        public readonly bool Array = Array;
        public readonly string Pass = Pass;
        public readonly string Field = Field;
        public readonly BindableScalar Value = Value;

        public int Changed;
        public int Generation;
        public double Last;
        public bool Written;

        // The binding's presented value through its registered slot, exact for an integer cell, or the source default
        // while the binding does not resolve; a literal is itself.
        public double Read(WorldStateMirror mirror) {
            if (Value.State is { } binding) {
                return (mirror.TryValue(
                    slot: mirror.SlotOf(
                        binding: in binding,
                        conversion: WorldStateConversion.Number
                    ),
                    value: out var bound
                )
                    ? bound
                    : Fallback
                );
            }

            return (((Value.Literal is { } literal) && float.IsFinite(f: literal))
                ? literal
                : Fallback
            );
        }
    }
}
