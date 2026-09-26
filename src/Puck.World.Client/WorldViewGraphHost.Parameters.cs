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
                    if (!Node.TryReadParameter(
                        field: field,
                        passName: pass,
                        value: out var fallback
                    )) {
                        Owner?.Report?.Invoke(
                            Name,
                            $"parameter refused: pass '{pass}' declares no scalar config field '{field}'"
                        );

                        continue;
                    }

                    bound.Add(item: new BoundParameter(
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
    }

    // One bound field and the value last written to it.
    private sealed class BoundParameter(string Pass, string Field, BindableScalar Value, double Fallback) {
        public readonly string Pass = Pass;
        public readonly string Field = Field;

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
