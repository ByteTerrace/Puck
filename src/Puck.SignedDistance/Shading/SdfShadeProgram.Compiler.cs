using System.Globalization;

namespace Puck.SignedDistance;

public sealed partial class SdfShadeProgram {
    private sealed record Node(SdfShadeOp Op, SdfShadeType Type, Node[] Args, uint[] Bits, uint Immediate, string Key, SdfPaintShape? Shape = null);
    private sealed class Compiler(SdfShadeDefinition source) {
        private readonly Dictionary<string, Node> m_nodes = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<string, SdfShadeShape> m_shapeDefinitions = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<SdfShadeExpression, Node>[] m_expressionCache = Enumerable.Range(count: 3, start: 0)
            .Select(selector: _ => new Dictionary<SdfShadeExpression, Node>(comparer: ReferenceEqualityComparer.Instance)).ToArray();
        private readonly Dictionary<(SdfShadePhase Phase, string Name), Node> m_values = [];
        private readonly List<SdfShadeValue> m_canonicalValues = [];
        private readonly List<SdfShadeParameter> m_parameters = [];
        private readonly Dictionary<int, int> m_slots = [];
        private readonly List<uint> m_constants = [];
        private readonly List<(SdfPaintShape Shape, int Relative)> m_shapes = [];
        private readonly List<(SdfShadeInstruction Op, int Word)> m_relocations = [];

        private SdfShadeReads m_reads;
        private int m_visits;

        public SdfShadeProgram Compile() {
            if (source is null || source.Name is null || source.Parameters is null || source.Outputs is null) { Fail("PUCK127", "A shade definition requires a name, parameters and outputs."); }
            if (source.Outputs.Count > 32) { Fail(code: "PUCK129", message: "Shade program exceeds its static capacity."); }
            if (source.Parameters.Any(p => p is null || string.IsNullOrEmpty(p.Name) || p.Bits is null)) { Fail("PUCK127", "A shade parameter requires a name, type and bits."); }
            if (source.Parameters.Select(selector: p => p.Name).Distinct(comparer: StringComparer.Ordinal).Count() != source.Parameters.Count) {
                Fail(code: "PUCK127", message: "Duplicate shade parameter.");
            }
            foreach (var parameter in source.Parameters) { ValidateBits(parameter.Type, parameter.Bits); }
            foreach (var value in (source.Values ?? [])) {
                if (value is null || string.IsNullOrEmpty(value.Name)) { Fail("PUCK127", "A shade value requires a name and expression."); }
                if (((uint)value.Phase) >= 3) { Fail(code: "PUCK126", message: "Unknown shade phase."); }
                var lowered = Lower(value.Value, value.Phase, 0);

                if (!m_values.TryAdd(key: (value.Phase, value.Name), value: lowered)) { Fail(code: "PUCK128", message: "Shade value is defined twice."); }
            }
            var outputs = new List<(SdfShadePhase Phase, string Name, Node Value)>();

            foreach (var output in source.Outputs) {
                if (output is null || output.Name is null) { Fail("PUCK127", "A shade output requires a name and expression."); }
                var type = OutputType(output.Phase, output.Name);
                var node = Lower(output.Value, output.Phase, 0);

                if (node.Type != type) { Fail(code: "PUCK127", message: $"Output '{output.Name}' needs {type}, got {node.Type}."); }
                if (outputs.Any(predicate: o => ((o.Phase == output.Phase) && (o.Name == output.Name)))) { Fail(code: "PUCK127", message: "Duplicate shade output."); }
                outputs.Add(item: (output.Phase, output.Name, node));
            }
            outputs.Sort(comparison: (a, b) => ((a.Phase != b.Phase) ? a.Phase.CompareTo(target: b.Phase) : StringComparer.Ordinal.Compare(x: a.Name, y: b.Name)));
            CanonicalizeParameters(outputs);
            var phases = new SdfShadeInstruction[3][];
            var bindings = new List<SdfShadeBinding>();
            var costs = new uint[3];
            var wrap = 0f;
            var tilt = 0f;
            var everySide = false;

            for (var phase = 0; (phase < 3); phase++) {
                var instructions = new List<SdfShadeInstruction>();
                var registers = new Dictionary<string, SdfShadeOperand>(comparer: StringComparer.Ordinal);

                foreach (var output in outputs.Where(predicate: o => (((int)o.Phase) == phase))) {
                    bindings.Add(item: new(output.Phase, output.Name, Emit(node: output.Value)));
                    if ((output.Phase == SdfShadePhase.Surface) && (output.Name == "wrap")) {
                        wrap = Math.Clamp(Upper(node: output.Value), 0f, 1f);
                    }
                    if ((output.Phase == SdfShadePhase.Surface) && (output.Name == "normal")) {
                        if ((output.Value.Op == SdfShadeOp.Input) && (SdfShadeOps.Inputs[((int)output.Value.Immediate)].Name == "surface.normal")) { } else if ((output.Value.Op == SdfShadeOp.Tilt) && (output.Value.Args[0].Op == SdfShadeOp.Input) &&
                                                                                                                                                     (SdfShadeOps.Inputs[((int)output.Value.Args[0].Immediate)].Name == "surface.normal")) {
                            tilt = Math.Clamp(Upper(node: output.Value.Args[2]), 0f, MathF.PI);
                        } else { everySide = true; }
                    }
                    // An arbitrary authored diffuse response is not proved to vanish behind the base terminator.
                    if ((output.Phase == SdfShadePhase.Light) && (output.Name is "diffuse" or "rim" or "specular") && (Upper(node: output.Value) > 0f)) { everySide = true; }
                }
                phases[phase] = instructions.ToArray();

                SdfShadeOperand Emit(Node node) {
                    if (registers.TryGetValue(key: node.Key, value: out var found)) { return found; }
                    var args = node.Args.Select(selector: Emit).ToArray();

                    if (instructions.Count >= MaxOps) { Fail(code: "PUCK129", message: $"Shade instruction count exceeds {MaxOps}."); }
                    var result = new SdfShadeOperand(index: instructions.Count, type: node.Type);
                    var immediate = node.Immediate;

                    if (node.Op == SdfShadeOp.Parameter) {
                        if (!m_slots.TryGetValue(key: ((int)immediate), value: out var slot)) {
                            if (m_parameters.Count >= MaxParameters) { Fail("PUCK129", $"Shade program exceeds {MaxParameters} live runtime slots."); }
                            slot = m_parameters.Count; m_slots.Add(key: ((int)immediate), value: slot); m_parameters.Add(item: source.Parameters[((int)immediate)]);
                        }
                        immediate = ((uint)slot);
                    }
                    if (node.Op == SdfShadeOp.Constant) {
                        immediate = ((uint)(m_constants.Count / 4));
                        m_constants.AddRange(collection: node.Bits);
                        while ((m_constants.Count % 4) != 0) { m_constants.Add(item: 0); }
                    }
                    if (node.Shape is { } shape) {
                        immediate = ((uint)(m_constants.Count / 4));
                        m_shapes.Add(item: (shape, ((int)immediate)));
                        m_constants.AddRange(collection: [((uint)shape.Type), ((uint)shape.Table.Count), (shape.Bound.IsUnbounded ? 1u : 0u), 0u]);
                        AddVector(vector: shape.Data0At(tableVector: (((int)immediate) + 7)));
                        AddVector(vector: shape.Data1);
                        AddVector(vector: new(value: shape.Scale, w: MathF.Min(x: shape.Scale.X, y: MathF.Min(x: shape.Scale.Y, y: shape.Scale.Z))));
                        AddVector(vector: new(value: shape.Bound.Center, w: 0f));
                        AddVector(vector: new(value: shape.Bound.HalfExtents, w: 0f));
                        AddVector(vector: new(shape.Rotation.X, shape.Rotation.Y, shape.Rotation.Z, shape.Rotation.W));
                        m_reads |= SdfShadeReads.Footprint;
                        m_constants.AddRange(collection: shape.Table.Select(selector: BitConverter.SingleToUInt32Bits));
                        while ((m_constants.Count % 4) != 0) { m_constants.Add(item: 0); }
                    }
                    if (node.Op == SdfShadeOp.Input) { m_reads |= SdfShadeOps.Inputs[((int)immediate)].Reads; }
                    var op = new SdfShadeInstruction(node.Op, result, args, immediate);

                    instructions.Add(item: op); registers.Add(key: node.Key, value: result);
                    m_canonicalValues.Add(item: new(((SdfShadePhase)phase), $"r{result.Index}", Canonical(args: args, node: node)));
                    costs[phase] = checked(((costs[phase] + SdfShadeOps.Get(op: node.Op).Work) + ((node.Shape is { } costShape) ? SdfShadeCost.Shape(shape: costShape) : 0u)));
                    if (checked(((costs[0] + costs[1]) + costs[2])) > MaxPhaseWork) { Fail(code: "PUCK129", message: $"Shade work exceeds {MaxPhaseWork}."); }
                    return result;
                }
            }
            var canonicalOutputs = bindings.Select(selector: o => new SdfShadeOutput(o.Phase, o.Name, new("value", Name: $"r{o.Value.Index}"))).ToArray();

            for (var phase = 0; (phase < phases.Length); phase++) {
                phases[phase] = Allocate(phases[phase], ((SdfShadePhase)phase), bindings);
                costs[phase] = 0;
                foreach (var instruction in phases[phase]) {
                    costs[phase] = checked(costs[phase] + SdfShadeOps.Get(instruction.Op).Work + (instruction.Op == SdfShadeOp.Shape
                        ? SdfShadeCost.Shape(m_shapes.First(s => s.Relative == instruction.Immediate).Shape) : 0u));
                }
            }
            if (costs.Aggregate(0UL, static (sum, cost) => checked(sum + cost)) > MaxPhaseWork) { Fail("PUCK129", $"Shade work exceeds {MaxPhaseWork} after register allocation."); }
            var pixelWork = checked(costs[0] + MaxLights * ((ulong)DefaultLightWork + costs[1]) + DefaultComposeWork + costs[2] + EngineWork);
            if (checked(128UL * pixelWork) > MaxWork) { Fail("PUCK129", "Shade work exceeds the dispatch bound for a complete glass group at the maximum light walk."); }
            // Header: instruction ranges for each phase, constant and layout ranges. Every range is vector relative.
            const int HeaderVectors = 4;
            const int InstructionVectors = 3;
            var words = new List<uint>(collection: Enumerable.Repeat(count: (HeaderVectors * 4), element: 0u));

            for (var phase = 0; (phase < 3); phase++) {
                words[(phase * 4)] = ((uint)(words.Count / 4)); words[((phase * 4) + 1)] = ((uint)phases[phase].Length);
                words[((phase * 4) + 2)] = costs[phase];
                foreach (var op in phases[phase]) {
                    var start = words.Count;

                    words.AddRange(collection: [((uint)op.Op), ((uint)op.Result.Index), ((uint)op.Result.Type) | (op.OutputMask << 8), op.Immediate]);
                    for (var arg = 0; (arg < 4); arg++) { words.Add(item: ((arg < op.Operands.Count) ? (uint)op.Operands[arg].Index : 0u)); }
                    for (var arg = 0; (arg < 4); arg++) { words.Add(item: ((arg < op.Operands.Count) ? (uint)op.Operands[arg].Type : 0u)); }
                    if (op.Op is SdfShadeOp.Constant or SdfShadeOp.Shape) { m_relocations.Add(item: (op, (start + 3))); }
                }
            }
            var constantsOffset = ((uint)(words.Count / 4));
            words[3] = (uint)m_reads;

            words[12] = constantsOffset; words[13] = ((uint)(m_constants.Count / 4));
            words.AddRange(collection: m_constants);
            foreach (var (shape, relative) in m_shapes) {
                var data0 = shape.Data0At(tableVector: checked(((((int)constantsOffset) + relative) + 7)));
                var start = checked((((((int)constantsOffset) + relative) + 1) * 4));

                words[start] = BitConverter.SingleToUInt32Bits(value: data0.X);
                RequireRange(((uint)(words.Count / 4)), checked(((constantsOffset + ((uint)relative)) + 7)), ((uint)((shape.Table.Count + 3) / 4)));
            }
            foreach (var (_, word) in m_relocations) { words[word] = checked((words[word] + constantsOffset)); }
            words[14] = ((uint)(words.Count / 4)); words[15] = ((uint)m_parameters.Count);
            var parameterWords = new List<uint>();

            foreach (var parameter in m_parameters) {
                words.AddRange(collection: [((uint)parameter.Type), 0u, 0u, 0u]);
                parameterWords.AddRange(collection: parameter.Bits);
                while ((parameterWords.Count % 4) != 0) { parameterWords.Add(item: 0u); }
            }
            // Output bindings are part of identity as well as the executable instructions.
            foreach (var binding in bindings) {
                words.AddRange(collection: [((uint)binding.Phase), OutputIndex(binding.Phase, binding.Name), ((uint)binding.Value.Index), ((uint)binding.Value.Type)]);
            }
            var sliceLength = ((uint)(words.Count / 4));

            for (var phase = 0; (phase < 3); phase++) {
                RequireRange(sliceLength, words[(phase * 4)], checked((words[((phase * 4) + 1)] * InstructionVectors)));
            }
            RequireRange(sliceLength, constantsOffset, ((uint)(m_constants.Count / 4)));
            RequireRange(sliceLength, words[14], words[15]);
            for (var phase = 0; (phase < 3); phase++) {
                for (var i = 0; (i < phases[phase].Length); i++) {
                    var op = phases[phase][i];

                    phases[phase][i] = new(op.Op, op.Result, op.Operands.ToArray(), words[(((((int)words[(phase * 4)]) * 4) + (i * 12)) + 3)], op.OutputMask);
                }
            }
            var canonical = new SdfShadeDefinition(source.Name,
                Array.AsReadOnly(array: m_parameters.Select(selector: (p, i) => new SdfShadeParameter($"p{i}", p.Type, Array.AsReadOnly(array: p.Bits.ToArray()))).ToArray()),
                Array.AsReadOnly(array: canonicalOutputs),
                Array.AsReadOnly(array: m_canonicalValues.ToArray()));

            return new SdfShadeProgram(canonical, words.ToArray(), parameterWords.ToArray(), m_parameters.Select(selector: p => p.Type).ToArray(),
                phases, bindings.ToArray(), costs, m_reads, new(EverySide: everySide, MaxTiltRadians: tilt, MaxWrap: wrap));

            SdfShadeExpression Canonical(Node node, SdfShadeOperand[] args) => new(SdfShadeOps.Get(op: node.Op).Name,
                ((args.Length == 0) ? null : Array.AsReadOnly(array: args.Select(selector: a => new SdfShadeExpression("value", Name: $"r{a.Index}")).ToArray())),
                node.Op switch {
                    SdfShadeOp.Parameter => $"p{m_slots[((int)node.Immediate)]}",
                    SdfShadeOp.Input => SdfShadeOps.Inputs[((int)node.Immediate)].Name,
                    SdfShadeOp.Component => node.Immediate.ToString(provider: CultureInfo.InvariantCulture),
                    _ => null,
                }, ((node.Op == SdfShadeOp.Constant) ? node.Type : SdfShadeType.Float),
                ((node.Op == SdfShadeOp.Constant) ? Array.AsReadOnly(array: node.Bits.ToArray()) : null),
                m_shapeDefinitions.GetValueOrDefault(key: node.Key));
        }

        // Parameter declarations are bindings, not code order. Label live slots by their uses in the output graph,
        // treating commutative edges as unordered. Data breaks only structurally indistinguishable ties, so renaming,
        // reordering or deleting an unused declaration cannot change executable code or misassociate its values.
        private void CanonicalizeParameters(List<(SdfShadePhase Phase, string Name, Node Value)> outputs) {
            var live = new HashSet<uint>();
            var visited = new HashSet<Node>(ReferenceEqualityComparer.Instance);
            foreach (var output in outputs) { Visit(output.Value); }
            void Visit(Node node) {
                if (!visited.Add(node)) { return; }
                if (node.Op == SdfShadeOp.Parameter) { live.Add(node.Immediate); }
                foreach (var arg in node.Args) { Visit(arg); }
            }
            var order = live.Select(slot => {
                var cache = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance);
                string Describe(Node node) {
                    if (cache.TryGetValue(node, out var found)) { return found; }
                    var args = node.Args.Select(Describe).ToArray();
                    if (SdfShadeOps.Get(node.Op).Commutative) { Array.Sort(args, StringComparer.Ordinal); }
                    var immediate = node.Op == SdfShadeOp.Parameter ? (node.Immediate == slot ? 1u : 0u) : node.Immediate;
                    return cache[node] = Key(node.Op, node.Type, immediate, node.Bits, args);
                }
                return (Slot: slot, Context: string.Join(';', outputs.Select(o => $"{(int)o.Phase}:{o.Name}:{Describe(o.Value)}")),
                    Data: string.Join(',', source.Parameters[(int)slot].Bits.Select(b => b.ToString("X8", CultureInfo.InvariantCulture))));
            }).OrderBy(p => p.Context, StringComparer.Ordinal).ThenBy(p => p.Data, StringComparer.Ordinal)
                .Select((p, i) => (p.Slot, Index: (uint)i)).ToDictionary(p => p.Slot, p => p.Index);
            var rewritten = new Dictionary<Node, Node>(ReferenceEqualityComparer.Instance);
            Node Rewrite(Node node) {
                if (rewritten.TryGetValue(node, out var found)) { return found; }
                var args = node.Args.Select(Rewrite).ToArray();
                if (SdfShadeOps.Get(node.Op).Commutative && string.CompareOrdinal(args[0].Key, args[1].Key) > 0) { (args[0], args[1]) = (args[1], args[0]); }
                var key = Key(node.Op, node.Type, node.Op == SdfShadeOp.Parameter ? order[node.Immediate] : node.Immediate, node.Bits, args.Select(a => a.Key));
                if (m_shapeDefinitions.TryGetValue(node.Key, out var shape)) { m_shapeDefinitions[key] = shape; }
                return rewritten[node] = node with { Args = args, Key = key };
            }
            for (var i = 0; i < outputs.Count; i++) { var output = outputs[i]; outputs[i] = (output.Phase, output.Name, Rewrite(output.Value)); }
        }

        private static string Key(SdfShadeOp op, SdfShadeType type, uint immediate, IEnumerable<uint> bits, IEnumerable<string> args) {
            var content = $"{(int)op}:{(int)type}:{immediate}:{string.Join(',', bits.Select(b => b.ToString("X8", CultureInfo.InvariantCulture)))}({string.Join(';', args)})";
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        }

        // Bounded named registers, with deterministic rematerialization instead of a GPU spill array. Canonical
        // values remain immutable: an evicted value is recomputed from its pure dependencies. The final stream,
        // including recomputation, is priced before admission. Outputs are written at every defining instruction.
        private static SdfShadeInstruction[] Allocate(SdfShadeInstruction[] instructions, SdfShadePhase phase, List<SdfShadeBinding> bindings) {
            var last = Enumerable.Range(0, instructions.Length).ToArray();

            for (var i = 0; (i < instructions.Length); i++) {
                foreach (var operand in instructions[i].Operands) { last[operand.Index] = i; }
            }
            var slots = Enumerable.Repeat(-1, instructions.Length).ToArray();
            var owners = Enumerable.Repeat(-1, MaxRegisters).ToArray();
            var masks = new uint[instructions.Length];
            var outputSlots = new int[instructions.Length];
            var pressure = new int[instructions.Length];
            for (var i = 0; i < instructions.Length; i++) {
                pressure[i] = Math.Max(1, instructions[i].Operands.Select(a => pressure[a.Index]).OrderDescending().Select((need, held) => need + held).DefaultIfEmpty(1).Max());
            }
            foreach (var output in bindings.Where(b => b.Phase == phase)) { masks[output.Value.Index] |= 1u << (int)OutputIndex(phase, output.Name); }
            var allocated = new List<SdfShadeInstruction>();
            var protectedValues = new HashSet<int>();
            var cursor = 0;
            for (; cursor < instructions.Length; cursor++) {
                _ = Ensure(cursor);
                for (var slot = 0; slot < owners.Length; slot++) {
                    if (owners[slot] is var owner && owner >= 0 && last[owner] <= cursor) { slots[owner] = -1; owners[slot] = -1; }
                }
            }

            int Ensure(int index) {
                if (slots[index] >= 0) { return slots[index]; }
                if (allocated.Count >= MaxOps) { Fail("PUCK129", "Shade rematerialization exceeds its instruction capacity."); }
                var instruction = instructions[index];
                var newlyProtected = new List<int>();
                var operands = new SdfShadeOperand[instruction.Operands.Count];
                foreach (var a in Enumerable.Range(0, operands.Length).OrderByDescending(a => pressure[instruction.Operands[a].Index])) {
                    var operand = instruction.Operands[a];
                    operands[a] = new(Ensure(operand.Index), operand.Type);
                    if (protectedValues.Add(operand.Index)) { newlyProtected.Add(operand.Index); }
                }
                // A source can become the destination: the interpreter reads all operands before writing it.
                foreach (var item in newlyProtected) { protectedValues.Remove(item); }
                var slot = Array.IndexOf(owners, -1);
                if (slot < 0) {
                    slot = Enumerable.Range(0, MaxRegisters).Where(s => !protectedValues.Contains(owners[s]))
                        .OrderBy(s => instructions[owners[s]].Operands.Count).ThenByDescending(s => last[owners[s]]).DefaultIfEmpty(-1).First();
                    if (slot < 0) { Fail("PUCK129", $"Shade expression needs more than {MaxRegisters} simultaneously held operands."); }
                    slots[owners[slot]] = -1;
                }
                owners[slot] = index; slots[index] = slot; outputSlots[index] = slot;
                if (allocated.Count >= MaxOps) { Fail("PUCK129", "Shade rematerialization exceeds its instruction capacity."); }
                allocated.Add(new(instruction.Op, new(slot, instruction.Result.Type), operands, instruction.Immediate, masks[index]));
                return slot;
            }
            for (var binding = 0; (binding < bindings.Count); binding++) {
                var output = bindings[binding];

                if (output.Phase == phase) { bindings[binding] = output with { Value = new(index: outputSlots[output.Value.Index], type: output.Value.Type) }; }
            }
            return allocated.ToArray();
        }
        private void AddVector(System.Numerics.Vector4 vector) => m_constants.AddRange(collection: [
            BitConverter.SingleToUInt32Bits(value: vector.X), BitConverter.SingleToUInt32Bits(value: vector.Y),
            BitConverter.SingleToUInt32Bits(value: vector.Z), BitConverter.SingleToUInt32Bits(value: vector.W)]);
        private Node Lower(SdfShadeExpression expression, SdfShadePhase phase, int depth) {
            if (expression is null || expression.Op is null) { Fail("PUCK127", "A shade expression requires an operation."); }
            if ((depth > 128) || (++m_visits > 100000)) { Fail(code: "PUCK129", message: "Shade expression expansion exceeds its capacity."); }
            if (m_expressionCache[((int)phase)].TryGetValue(key: expression, value: out var cached)) { return cached; }
            if (expression.Op == "value") {
                return (m_values.GetValueOrDefault(key: (phase, (expression.Name ?? "")))
                    ?? throw new SdfShadeException(code: "PUCK128", message: $"Shade value '{expression.Name}' is undefined in {phase}."));
            }
            var info = (SdfShadeOps.Table.FirstOrDefault(predicate: o => (o.Name == expression.Op))
                ?? throw new SdfShadeException(code: "PUCK126", message: $"Unknown shade operation '{expression.Op}'."));
            var args = (expression.Arguments ?? []).Select(selector: a => Lower(depth: (depth + 1), expression: a, phase: phase)).ToArray();

            if (args.Length != info.Arity) { Fail(code: "PUCK127", message: $"'{info.Name}' takes {info.Arity} operands."); }
            var immediate = 0U;
            uint[] bits = [];
            SdfPaintShape? shape = null;
            SdfShadeType type;

            switch (info.Op) {
                case SdfShadeOp.Constant:
                    bits = (expression.Bits ?? []).ToArray(); ValidateBits(expression.Type, bits); type = expression.Type; break;
                case SdfShadeOp.Parameter:
                    var slot = source.Parameters.ToList().FindIndex(match: p => (p.Name == expression.Name));
                    if (slot < 0) { Fail(code: "PUCK128", message: $"Undefined shade parameter '{expression.Name}'."); }
                    immediate = ((uint)slot); type = source.Parameters[slot].Type; break;
                case SdfShadeOp.Input:
                    var input = SdfShadeOps.Inputs.ToList().FindIndex(match: i => (i.Name == expression.Name));
                    if (input < 0) { Fail(code: "PUCK126", message: $"Unknown shade stamp member '{expression.Name}'."); }
                    var field = SdfShadeOps.Inputs[input];
                    if (field.AfterSurface && (phase == SdfShadePhase.Surface)) { Fail(code: "PUCK126", message: $"'{field.Name}' is available after the surface phase."); }
                    if ((field.Phase is { } required) && (phase != required)) { Fail(code: "PUCK126", message: $"'{field.Name}' is available only in {required}."); }
                    type = field.Type; immediate = ((uint)input); break;
                case SdfShadeOp.Component:
                    if (!uint.TryParse(expression.Name, NumberStyles.None, CultureInfo.InvariantCulture, out immediate) || (immediate >= SdfShadeOps.Width(type: args[0].Type))) {
                        Fail(code: "PUCK127", message: "Component is outside its typed vector.");
                    }
                    type = (SdfShadeOps.IsUInt(type: args[0].Type) ? SdfShadeType.UInt : SdfShadeType.Float); break;
                case SdfShadeOp.Shape:
                    type = TypeOf(info.Op, args.Select(selector: a => a.Type).ToArray());
                    try {
                        var descriptor = (expression.Shape ?? throw new SdfShadeException(code: "PUCK127", message: "shape requires a structural descriptor.")).Canonicalize();
                        expression = expression with { Shape = descriptor };
                        shape = descriptor.Capture();
                    } catch (ArgumentException error) { throw new SdfShadeException(code: "PUCK127", message: error.Message); }
                    var shapeWords = new List<uint> { ((uint)shape.Type) };
                    foreach (var v in new[] { shape.Data0At(tableVector: 0), shape.Data1, new System.Numerics.Vector4(value: shape.Scale, w: 0),
                        new System.Numerics.Vector4(value: shape.Bound.Center, w: (shape.Bound.IsUnbounded ? 1f : 0f)), new System.Numerics.Vector4(value: shape.Bound.HalfExtents, w: 0f),
                        new System.Numerics.Vector4(shape.Rotation.X, shape.Rotation.Y, shape.Rotation.Z, shape.Rotation.W) }) {
                        shapeWords.AddRange(collection: [BitConverter.SingleToUInt32Bits(value: v.X), BitConverter.SingleToUInt32Bits(value: v.Y), BitConverter.SingleToUInt32Bits(value: v.Z), BitConverter.SingleToUInt32Bits(value: v.W)]);
                    }
                    shapeWords.AddRange(collection: shape.Table.Select(selector: BitConverter.SingleToUInt32Bits)); bits = shapeWords.ToArray(); break;
                default: type = TypeOf(info.Op, args.Select(selector: a => a.Type).ToArray()); break;
            }
            if (info.Commutative && (string.CompareOrdinal(strA: args[0].Key, strB: args[1].Key) > 0)) { (args[0], args[1]) = (args[1], args[0]); }
            if ((info.Op == SdfShadeOp.Select) && (args[0].Op == SdfShadeOp.Constant)) {
                return ((BitConverter.UInt32BitsToSingle(value: args[0].Bits[0]) == 0f) ? args[2] : args[1]);
            }
            if (Fold(info.Op, type, args, out var folded)) {
                info = SdfShadeOps.Get(op: SdfShadeOp.Constant); bits = folded; args = []; immediate = 0;
            }
            var key = Key(info.Op, type, immediate, bits, args.Select(a => a.Key));

            if (expression.Shape is { } definition) {
                m_shapeDefinitions.TryAdd(key: key, value: definition with {
                    Profile = ((definition.Profile is { Vertices: { } vertices } profile)
                        ? profile with { Vertices = Array.AsReadOnly(array: vertices.ToArray()) } : definition.Profile),
                });
            }
            if (!m_nodes.TryGetValue(key: key, value: out var node)) { node = new(info.Op, type, args, bits, immediate, key, shape); m_nodes.Add(key: key, value: node); }
            m_expressionCache[((int)phase)][expression] = node;
            return node;
        }
    }

    private static readonly string[][] OutputNames = [
        ["albedo", "roughness", "metal", "specular", "coat", "sheen", "wrap", "fill", "emissive", "normal", "reveal"],
        ["diffuse", "rim", "specular", "attenuation"], ["color"],
    ];

    /// <summary>The largest named output set of any phase, used by the shared generator.</summary>
    public static int OutputCapacity => OutputNames.Max(selector: static names => names.Length);

    /// <summary>Returns the required output type, refusing a name outside its phase.</summary>
    public static SdfShadeType OutputType(SdfShadePhase phase, string name) {
        _ = OutputIndex(name: name, phase: phase);
        return (((name is "albedo" or "fill" or "normal" or "diffuse" or "rim" or "color") ||
            ((phase == SdfShadePhase.Light) && (name == "specular"))) ? SdfShadeType.Float3 : SdfShadeType.Float);
    }

    private static uint OutputIndex(SdfShadePhase phase, string name) {
        if (((uint)phase) >= OutputNames.Length) { Fail(code: "PUCK126", message: "Unknown shade phase."); }
        var index = Array.IndexOf(array: OutputNames[((int)phase)], value: name);

        if (index < 0) { Fail(code: "PUCK126", message: $"Unknown {phase} output '{name}'."); }
        return ((uint)index);
    }
    private static void ValidateBits(SdfShadeType type, IReadOnlyList<uint>? bits) {
        if (bits is null) { Fail("PUCK127", "Literal or parameter bits are required."); }
        if (bits.Count != SdfShadeOps.Width(type: type)) { Fail(code: "PUCK127", message: "Literal or parameter has the wrong typed width."); }
        // Source constants and parameters are finite. IEEE exceptional results may arise inside arithmetic.
        if (!SdfShadeOps.IsUInt(type: type) && bits.Any(predicate: b => !float.IsFinite(f: BitConverter.UInt32BitsToSingle(value: b)))) {
            Fail(code: "PUCK127", message: "Authored shade constants and parameters must be finite.");
        }
    }
    private static void RequireRange(uint length, uint start, uint count) {
        if (!ContainsRange(length: count, offset: start, sliceLength: length)) { Fail(code: "PUCK127", message: "Shade operand escapes its own slice."); }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new SdfShadeException(code: code, message: message);
}
