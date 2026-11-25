using System.Collections.Immutable;
using System.IO.Hashing;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Puck.Assets;

/// <summary>The normalized IL and type layouts reachable from production entry methods in a deployed Puck build.
/// Calls, delegates, constructed overrides and static field data join the identity; unrelated members do not.
/// Reflection and callbacks into types the roots never construct require explicit roots.</summary>
public sealed partial class CodeClosure {
    private readonly HashSet<string> m_types;
    private CodeClosure(SortedDictionary<string, UInt128> members, SortedDictionary<string, string> reasons, HashSet<string> types) {
        m_types = types;
        Members = members;
        Reasons = reasons;
        var hash = new XxHash128();
        Span<byte> value = stackalloc byte[16];
        foreach (var (member, memberHash) in members) {
            hash.Append(source: Encoding.UTF8.GetBytes(s: member));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt128LittleEndian(destination: value, value: memberHash);
            hash.Append(source: value);
        }
        Hash = hash.GetCurrentHashAsUInt128();
    }
    /// <summary>Gets the identity of the reachable code and layouts.</summary>
    public UInt128 Hash { get; }
    /// <summary>Gets normalized member names and their hashes in ordinal order.</summary>
    public IReadOnlyDictionary<string, UInt128> Members { get; }
    /// <summary>Gets the first member reaching each member, or root.</summary>
    public IReadOnlyDictionary<string, string> Reasons { get; }
    /// <summary>Gets each <c>Puck</c> assembly the walk read and its module version identity, the empty identity for one
    /// the build does not hold, in ordinal order; with a shared <see cref="ModuleSet"/>, also the assemblies its earlier
    /// walks read. The hash depends on nothing else in the build (<see cref="CodeIdentity"/>).</summary>
    public IReadOnlyDictionary<string, Guid> Assemblies { get; private init; } = new SortedDictionary<string, Guid>(comparer: StringComparer.Ordinal);
    /// <summary>Whether an applied metadata update to this type invalidates this closure.</summary>
    /// <param name="type">The updated type.</param>
    /// <returns>Whether the closure reads it.</returns>
    public bool Reads(Type type) {
        ArgumentNullException.ThrowIfNull(argument: type);
        var name = CanonicalTypeName(name: type.FullName ?? type.Name);
        return m_types.Contains(item: ((type.Assembly.GetName().Name ?? string.Empty) + "!" + name));
    }
    /// <summary>Walks roots in a build without loading its assemblies. Missing roots fail by name.</summary>
    /// <param name="directory">The build directory.</param>
    /// <param name="roots">Assembly, full type name and method name (all overloads), or null for the whole type.</param>
    /// <param name="stopTypes">Full names of the types whose called bodies are transport or cache boundaries, a nested type
    /// spelled <c>Outer/Nested</c>; a stop type stops the types nested in it, and explicit roots still run.</param>
    /// <returns>The immutable closure.</returns>
    public static CodeClosure Of(string directory, IReadOnlyList<(string Assembly, string Type, string? Method)> roots, IReadOnlySet<string>? stopTypes = null) {
        using var modules = new ModuleSet(directory);
        return modules.Of(roots, stopTypes);
    }

    /// <summary>Assembly images shared by independent closure walks of one deployed build.</summary>
    public sealed class ModuleSet(string directory) : IDisposable {
        private readonly string m_directory = Path.GetFullPath(directory);
        private readonly Dictionary<string, Module?> m_modules = new(StringComparer.Ordinal);
        private readonly Dictionary<MetadataReader, Module> m_byReader = [];

        /// <summary>Walks these roots with an independent reachability set.</summary>
        public CodeClosure Of(IReadOnlyList<(string Assembly, string Type, string? Method)> roots, IReadOnlySet<string>? stopTypes = null) {
            ArgumentNullException.ThrowIfNull(roots);
            var walk = new Walk(m_directory, stopTypes ?? new HashSet<string>(), Load, m_byReader);
            var members = walk.Run(roots);
            var assemblies = new SortedDictionary<string, Guid>(comparer: StringComparer.Ordinal);

            foreach (var (name, module) in m_modules) {
                if (name.StartsWith("Puck.", StringComparison.Ordinal)) {
                    assemblies[name] = ((module is null) ? Guid.Empty : module.Metadata.GetGuid(handle: module.Metadata.GetModuleDefinition().Mvid));
                }
            }
            return new CodeClosure(members, walk.Reasons, walk.Types) { Assemblies = assemblies };
        }

        private Module? Load(string name) {
            if (m_modules.TryGetValue(name, out var held)) { return held; }
            var path = Path.Combine(m_directory, name + ".dll");
            var module = name.StartsWith("Puck.", StringComparison.Ordinal) && File.Exists(path) ? new Module(name, File.ReadAllBytes(path)) : null;
            m_modules.Add(name, module);
            if (module is not null) { m_byReader.Add(module.Metadata, module); }
            return module;
        }

        /// <summary>Reads an unsigned constant from the same images the walks use.</summary>
        public ulong ReadUInt64Constant(string assembly, string type, string field) {
            var module = Load(assembly) ?? throw new FileNotFoundException(assembly);
            foreach (var handle in module.Metadata.GetTypeDefinition(handle: module.Types[type]).GetFields()) {
                var definition = module.Metadata.GetFieldDefinition(handle: handle);
                if (module.Metadata.StringComparer.Equals(handle: definition.Name, value: field) && definition.GetDefaultValue() is { IsNil: false } constant) {
                    return module.Metadata.GetBlobReader(handle: module.Metadata.GetConstant(handle: constant).Value).ReadUInt64();
                }
            }
            throw new KeyNotFoundException(message: $"{assembly}!{type}.{field}");
        }

        /// <inheritdoc/>
        public void Dispose() {
            foreach (var module in m_modules.Values) { module?.Pe.Dispose(); }
            m_modules.Clear();
            m_byReader.Clear();
        }
    }
    /// <summary>Returns the members whose hash differs between two closures' members, with those only one holds, in
    /// ordinal order.</summary>
    /// <param name="members">The later closure's members (<see cref="Members"/>).</param>
    /// <param name="earlier">The earlier closure's members.</param>
    /// <returns>The changed, added and removed members.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> or <paramref name="earlier"/> is
    /// <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, UInt128> members, IReadOnlyDictionary<string, UInt128> earlier) {
        ArgumentNullException.ThrowIfNull(argument: members);
        ArgumentNullException.ThrowIfNull(argument: earlier);

        var changed = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var (member, hash) in members) {
            if (!earlier.TryGetValue(key: member, value: out var before) || (before != hash)) { _ = changed.Add(item: member); }
        }
        foreach (var member in earlier.Keys) {
            if (!members.ContainsKey(key: member)) { _ = changed.Add(item: member); }
        }

        return [.. changed];
    }

    // A file-local type's name carries a checksum of its file's path, which another checkout spells otherwise.
    [System.Text.RegularExpressions.GeneratedRegex(pattern: "F[0-9A-F]{64}__")]
    private static partial System.Text.RegularExpressions.Regex FileLocalChecksum();

    private static string CanonicalTypeName(string name) => FileLocalChecksum().Replace(input: name, replacement: "__").Replace(oldChar: '+', newChar: '/');

    // A compiler-generated name (a lambda, a local function, a closure class, a cached delegate or a state machine) carries
    // the ordinal of the method that declares it among its type's members: <M>b__O_L, <M>g__F|O_L, <>c__DisplayClassO_L,
    // <>9__O_L and <M>d__O. The ordinal moves whenever a member is added or removed above that method, though nothing the
    // method does changed.
    [System.Text.RegularExpressions.GeneratedRegex(pattern: @"^(?<pre><[^>]*>b__|<[^>]*>g__[^|]*\||<>c__DisplayClass|<>9__)(?<o>\d+)(?<post>_\d+(`\d+)?)$|^(?<pre><[^>]*>d__)(?<o>\d+)(?<post>(`\d+)?)$")]
    private static partial System.Text.RegularExpressions.Regex GeneratedOrdinal();

    // One assembly the walk reads: its metadata, its image and its types by full name.
    private sealed class Module {
        public Module(string name, byte[] image) {
            Name = name;
            Pe = new PEReader(peImage: System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(image));
            Metadata = Pe.GetMetadataReader();
            foreach (var handle in Metadata.TypeDefinitions) {
                Types[FullName(handle: handle)] = handle;
            }
        }

        public string Name { get; }
        public PEReader Pe { get; }
        public MetadataReader Metadata { get; }
        public Dictionary<string, TypeDefinitionHandle> Types { get; } = new(comparer: StringComparer.Ordinal);

        public string FullName(TypeDefinitionHandle handle) {
            var type = Metadata.GetTypeDefinition(handle: handle);
            var name = CanonicalTypeName(name: Metadata.GetString(handle: type.Name));
            var declaring = type.GetDeclaringType();
            var space = Metadata.GetString(handle: type.Namespace);

            return ((!declaring.IsNil) ? (FullName(handle: declaring) + "/" + Canonical(declaring: declaring, name: name)) : ((space.Length == 0) ? name : (space + "." + name)));
        }
        public string NameOf(MethodDefinitionHandle method) {
            var definition = Metadata.GetMethodDefinition(handle: method);

            return Canonical(declaring: definition.GetDeclaringType(), name: Metadata.GetString(handle: definition.Name));
        }
        public string NameOf(FieldDefinitionHandle field) {
            var definition = Metadata.GetFieldDefinition(handle: field);

            return Canonical(declaring: definition.GetDeclaringType(), name: Metadata.GetString(handle: definition.Name));
        }

        // Each user type's generated ordinals, written as the name of the method that declares them.
        private readonly Dictionary<TypeDefinitionHandle, Dictionary<string, string>> m_ordinals = [];

        // A member or nested type's name with a generated ordinal written as its method's name (GeneratedOrdinal), so an
        // edit elsewhere in the type renames nothing the closure runs. Overloads that each declare generated members keep
        // apart by their order among themselves; an ordinal whose method no generated name shows stays as it is.
        public string Canonical(TypeDefinitionHandle declaring, string name) {
            var match = GeneratedOrdinal().Match(input: name);

            if (!match.Success) { return name; }

            var scope = declaring;

            for (var type = Metadata.GetTypeDefinition(handle: scope); Metadata.GetString(handle: type.Name).StartsWith(value: '<') && !type.GetDeclaringType().IsNil; type = Metadata.GetTypeDefinition(handle: scope)) {
                scope = type.GetDeclaringType();
            }

            return (OrdinalsOf(scope: scope).TryGetValue(key: match.Groups["o"].Value, value: out var method) ? (match.Groups["pre"].Value + method + match.Groups["post"].Value) : name);
        }
        private Dictionary<string, string> OrdinalsOf(TypeDefinitionHandle scope) {
            if (m_ordinals.TryGetValue(key: scope, value: out var held)) { return held; }

            var methods = new SortedDictionary<int, string>();
            var pending = new Stack<TypeDefinitionHandle>(collection: [scope]);

            while (pending.TryPop(result: out var handle)) {
                var type = Metadata.GetTypeDefinition(handle: handle);

                foreach (var method in type.GetMethods()) {
                    var name = Metadata.GetString(handle: Metadata.GetMethodDefinition(handle: method).Name);

                    if (GeneratedOrdinal().Match(input: name) is { Success: true } named && name.StartsWith(value: '<') && (name[1] != '>')) {
                        _ = methods.TryAdd(key: int.Parse(s: named.Groups["o"].ValueSpan, provider: System.Globalization.CultureInfo.InvariantCulture), value: name[1..name.IndexOf(value: '>')]);
                    }
                }
                foreach (var nested in type.GetNestedTypes()) {
                    var name = Metadata.GetString(handle: Metadata.GetTypeDefinition(handle: nested).Name);

                    if (!name.StartsWith(value: '<')) { continue; }
                    pending.Push(item: nested);
                    if (GeneratedOrdinal().Match(input: name) is not { Success: true } named) { continue; }

                    var ordinal = int.Parse(s: named.Groups["o"].ValueSpan, provider: System.Globalization.CultureInfo.InvariantCulture);

                    if (name[1] != '>') {
                        _ = methods.TryAdd(key: ordinal, value: name[1..name.IndexOf(value: '>')]);
                    } else if (Metadata.GetTypeDefinition(handle: nested).GetMethods().Select(selector: member => Metadata.GetString(handle: Metadata.GetMethodDefinition(handle: member).Name)).FirstOrDefault(predicate: member => member.StartsWith(value: '<') && (member.Length > 1) && (member[1] != '>')) is { } owned) {
                        _ = methods.TryAdd(key: ordinal, value: owned[1..owned.IndexOf(value: '>')]);
                    }
                }
            }

            var ordinals = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

            foreach (var group in methods.GroupBy(keySelector: static pair => pair.Value, comparer: StringComparer.Ordinal)) {
                var rank = 0;
                var overloads = group.Count();

                foreach (var (ordinal, method) in group) {
                    ordinals[ordinal.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)] = ((overloads == 1) ? method : (method + "~" + rank));
                    rank++;
                }
            }

            m_ordinals[scope] = ordinals;

            return ordinals;
        }
    }

    // The walk over one build's assemblies, from the roots to every member they reach.
    private sealed class Walk(string directory, IReadOnlySet<string> stopTypes, Func<string, Module?> load, Dictionary<MetadataReader, Module> readers) : ISignatureTypeProvider<string, object?> {
        private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(bindingAttr: (BindingFlags.Public | BindingFlags.Static))
            .Select(selector: static field => (OpCode)field.GetValue(obj: null)!)
            .ToDictionary(keySelector: static code => unchecked((ushort)code.Value));
        // The bytes each operand type takes after its opcode, indexed by OperandType's value (InlineBrTarget 0, InlineField,
        // InlineI, InlineI8, InlineMethod, InlineNone, InlinePhi, InlineR 7, 8 unused, InlineSig 9, InlineString,
        // InlineSwitch, InlineTok, InlineType, InlineVar 14, ShortInlineBrTarget, ShortInlineI, ShortInlineR,
        // ShortInlineVar 18); a token or a switch's targets are read apart.
        private static readonly int[] OperandBytes = [4, 4, 4, 8, 4, 0, 0, 8, 0, 4, 4, 0, 4, 4, 2, 1, 1, 4, 1];

        private readonly SortedDictionary<string, UInt128> m_members = new(comparer: StringComparer.Ordinal);
        private readonly HashSet<(Module, MethodDefinitionHandle)> m_methodsSeen = [];
        private readonly Queue<(Module, MethodDefinitionHandle)> m_methods = new();
        private readonly HashSet<(Module, TypeDefinitionHandle)> m_typesSeen = [];
        private readonly Queue<(Module, TypeDefinitionHandle)> m_types = new();
        private readonly HashSet<(Module, TypeDefinitionHandle)> m_constructed = [];
        // The fields the closure's code loads, stores or takes the address of, and the types whose layout counts only those.
        private readonly HashSet<(Module, FieldDefinitionHandle)> m_touched = [];
        private readonly List<(Module, TypeDefinitionHandle)> m_byTouch = [];
        // The member each entity was first reached from, and the member being hashed, which reaches whatever it names.
        private readonly Dictionary<(Module, EntityHandle), string> m_from = [];
        private string m_current = "root";
        // While a layout or a signature is named, the types it names join nothing.
        private bool m_quiet;

        public SortedDictionary<string, string> Reasons { get; } = new(comparer: StringComparer.Ordinal);
        // The type of each member the walk hashes, as "assembly!type".
        public HashSet<string> Types { get; } = new(comparer: StringComparer.Ordinal);

        private void Declares(Module module, TypeDefinitionHandle type) => _ = Types.Add(item: (module.Name + "!" + module.FullName(handle: type)));

        public SortedDictionary<string, UInt128> Run(IReadOnlyList<(string Assembly, string Type, string? Method)> roots) {
            foreach (var (assembly, type, method) in roots) {
                var module = (load(assembly) ?? throw new FileNotFoundException(message: $"'{directory}' holds no {assembly}.dll.", fileName: Path.Combine(path1: directory, path2: (assembly + ".dll"))));

                Root(method: method, module: module, type: module.Types[CanonicalTypeName(name: type)]);
            }
            while ((m_methods.Count > 0) || (m_types.Count > 0)) {
                if (m_types.TryDequeue(result: out var type)) {
                    HashType(module: type.Item1, type: type.Item2);
                } else {
                    var (module, method) = m_methods.Dequeue();

                    HashMethod(method: method, module: module);
                }
            }
            foreach (var (module, type) in m_byTouch) {
                HashLayout(module: module, type: type, touchedOnly: !m_constructed.Contains(item: (module, type)) || !IsRecord(module: module, type: type));
            }

            return m_members;
        }

        // A root type's methods of a name, or every method of it and of the types nested in it.
        private void Root(Module module, TypeDefinitionHandle type, string? method) {
            var definition = module.Metadata.GetTypeDefinition(handle: type);

            var found = false;
            foreach (var handle in definition.GetMethods()) {
                if ((method is null) || module.Metadata.StringComparer.Equals(handle: module.Metadata.GetMethodDefinition(handle: handle).Name, value: method)) {
                    found = true;
                    Enqueue(method: handle, module: module, root: true);
                }
            }
            if ((method is not null) && !found) {
                throw new KeyNotFoundException($"Root method {module.Name}!{module.FullName(type)}.{method} is absent.");
            }
            foreach (var nested in ((method is null) ? definition.GetNestedTypes() : [])) {
                Root(method: null, module: module, type: nested);
            }
        }
        // The Puck type a type token names, through a generic instantiation to its definition; null outside Puck.
        private (Module, TypeDefinitionHandle)? Resolve(Module module, EntityHandle handle) {
            var reader = module.Metadata;

            if (handle.Kind == HandleKind.TypeDefinition) { return (module, (TypeDefinitionHandle)handle); }
            if (handle.Kind == HandleKind.TypeReference) {
                var reference = reader.GetTypeReference(handle: (TypeReferenceHandle)handle);
                var name = reader.GetString(handle: reference.Name);
                var space = reader.GetString(handle: reference.Namespace);
                var scope = reference.ResolutionScope;
                // A nested type's reference is scoped by its declaring type's; any other scope here is an assembly.
                var outer = ((scope.Kind == HandleKind.TypeReference) ? Resolve(handle: (EntityHandle)scope, module: module) : null);
                var target = ((outer is { } declaring) ? declaring.Item1 : ((scope.Kind == HandleKind.AssemblyReference) ? load(reader.GetString(handle: reader.GetAssemblyReference(handle: (AssemblyReferenceHandle)scope).Name)) : null));
                var full = ((outer is { } within) ? (within.Item1.FullName(handle: within.Item2) + "/" + name) : ((space.Length == 0) ? name : (space + "." + name)));

                return (((target is not null) && target.Types.TryGetValue(key: full, value: out var definition)) ? (target, definition) : null);
            }

            // A type specification: a generic instantiation names its definition next; any other names no Puck type.
            var blob = reader.GetBlobReader(handle: reader.GetTypeSpecification(handle: (TypeSpecificationHandle)handle).Signature);

            return ((blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance) && (blob.ReadSignatureTypeCode() is SignatureTypeCode.TypeHandle) ? Resolve(handle: blob.ReadTypeHandle(), module: module) : null);
        }
        // A stop type stops the types nested in it too; a nested type named alone stops only itself and its own nested types.
        private bool Stopped(Module module, TypeDefinitionHandle type) {
            for (var name = module.FullName(handle: type); ; name = name[..name.LastIndexOf(value: '/')]) {
                if (stopTypes.Contains(item: name)) { return true; }
                if (!name.Contains(value: '/')) { return false; }
            }
        }
        private void Enqueue(Module module, MethodDefinitionHandle method, bool root = false) {
            if ((root || !Stopped(module: module, type: module.Metadata.GetMethodDefinition(handle: method).GetDeclaringType())) && m_methodsSeen.Add(item: (module, method))) {
                m_methods.Enqueue(item: (module, method));
                m_from[(module, method)] = (root ? "root" : m_current);
            }
        }
        // A type the closure names: its layout is hashed.
        private void Mention(Module module, TypeDefinitionHandle type) {
            if (!m_quiet && !Stopped(module: module, type: type) && m_typesSeen.Add(item: (module, type))) {
                m_types.Enqueue(item: (module, type));
                m_from[(module, type)] = m_current;
            }
        }
        // A type whose static member the closure touches: its static constructor runs first.
        private void Initialize(Module module, TypeDefinitionHandle type) {
            foreach (var handle in module.Metadata.GetTypeDefinition(handle: type).GetMethods()) {
                if (module.Metadata.StringComparer.Equals(handle: module.Metadata.GetMethodDefinition(handle: handle).Name, value: ".cctor")) { Enqueue(method: handle, module: module); }
            }
        }
        // A type whose instances the closure makes: its static constructor runs, and any override it or its base types
        // declare may run, through the runtime's interfaces as through Puck's, but a record's synthesized members, whose
        // behavior its hashed layout decides. This is the only way a virtual call reaches an implementation: a call
        // through an abstract or interface member runs on an instance some code made, so the members of the types the
        // closure makes are every implementation such a call can reach, and a type that merely derives from the callee's
        // (a job unit another caller posts to the same lane, an evaluator the walk never builds) joins nothing.
        private void Construct(Module module, TypeDefinitionHandle type) {
            if (!m_constructed.Add(item: (module, type))) { return; }

            var definition = module.Metadata.GetTypeDefinition(handle: type);

            Mention(module: module, type: type);
            Initialize(module: module, type: type);
            foreach (var method in definition.GetMethods()) {
                var candidate = module.Metadata.GetMethodDefinition(handle: method);

                if (((candidate.Attributes & MethodAttributes.Virtual) != 0) && !IsCompilerGenerated(attributes: candidate.GetCustomAttributes(), module: module)) { Enqueue(method: method, module: module); }
            }
            if (!definition.BaseType.IsNil && (Resolve(handle: definition.BaseType, module: module) is { } parent)) { Construct(module: parent.Item1, type: parent.Item2); }
        }
        private static bool IsCompilerGenerated(Module module, CustomAttributeHandleCollection attributes) => attributes
            .Select(selector: handle => module.Metadata.GetCustomAttribute(handle: handle).Constructor)
            .Any(predicate: constructor => ((constructor.Kind == HandleKind.MemberReference) && (module.Metadata.GetMemberReference(handle: (MemberReferenceHandle)constructor).Parent is { Kind: HandleKind.TypeReference } parent) && module.Metadata.StringComparer.Equals(handle: module.Metadata.GetTypeReference(handle: (TypeReferenceHandle)parent).Name, value: "CompilerGeneratedAttribute")));
        // A value type's layout is the bytes its code moves, so it counts whole; any other type's layout counts the fields
        // the closure's code reads or writes (HashLayout, once the walk ends and every such field is known), and a field the
        // code never touches cannot change what it does.
        private void HashType(Module module, TypeDefinitionHandle type) {
            var definition = module.Metadata.GetTypeDefinition(handle: type);
            var baseName = (definition.BaseType.IsNil ? string.Empty : Quiet(name: () => TypeName(handle: definition.BaseType, module: module)));

            m_current = (module.Name + "!" + module.FullName(handle: type) + " [layout]");
            Reasons[m_current] = m_from[(module, type)];
            Declares(module: module, type: type);
            if (!definition.BaseType.IsNil && (Resolve(handle: definition.BaseType, module: module) is { } parent)) { Mention(module: parent.Item1, type: parent.Item2); }
            if (baseName is "System.ValueType" or "System.Enum") {
                // A value type is made wherever it is named, with no constructor call to show it.
                Construct(module: module, type: type);
                HashLayout(module: module, type: type, touchedOnly: false);
            } else {
                m_byTouch.Add(item: (module, type));
            }
        }
        private void HashLayout(Module module, TypeDefinitionHandle type, bool touchedOnly) {
            var reader = module.Metadata;
            var definition = reader.GetTypeDefinition(handle: type);
            var text = new StringBuilder();
            var layout = definition.GetLayout();
            var key = (module.Name + "!" + module.FullName(handle: type) + " [layout]");
            var baseName = (definition.BaseType.IsNil ? string.Empty : Quiet(name: () => TypeName(handle: definition.BaseType, module: module)));

            text.Append(value: (int)definition.Attributes).Append(value: ' ').Append(value: layout.PackingSize).Append(value: ' ').Append(value: layout.Size).Append(value: " : ").Append(value: baseName);
            foreach (var handle in definition.GetFields()) {
                if (touchedOnly && !m_touched.Contains(item: (module, handle))) { continue; }

                var field = reader.GetFieldDefinition(handle: handle);

                text.Append(value: '\n').Append(value: (int)field.Attributes).Append(value: ' ').Append(value: module.NameOf(field: handle)).Append(value: ' ').Append(value: Quiet(name: () => field.DecodeSignature(genericContext: null, provider: this))).Append(value: ' ').Append(value: field.GetOffset());
            }

            m_members[key] = XxHash128.HashToUInt128(source: Encoding.UTF8.GetBytes(s: text.ToString()));
        }
        // A record's synthesized equality, hash and printing read every field, and run through the runtime where the walk
        // does not follow, so a record the closure makes counts its whole layout.
        private static bool IsRecord(Module module, TypeDefinitionHandle type) => module.Metadata.GetTypeDefinition(handle: type).GetMethods()
            .Any(predicate: handle => module.Metadata.StringComparer.Equals(handle: module.Metadata.GetMethodDefinition(handle: handle).Name, value: "<Clone>$"));
        // A record's synthesized clone and copy constructor move every field and decide nothing: they are hashed by their
        // signatures, so a field the closure never reads does not change them.
        private bool IsCopy(Module module, MethodDefinitionHandle method, string signature) {
            var definition = module.Metadata.GetMethodDefinition(handle: method);
            var declaring = definition.GetDeclaringType();
            var name = module.Metadata.GetString(handle: definition.Name);

            return (IsRecord(module: module, type: declaring) && ((name == "<Clone>$") || ((name == ".ctor") && signature.EndsWith(value: ("(" + module.FullName(handle: declaring) + ")Void instance"), comparisonType: StringComparison.Ordinal))));
        }
        private void HashMethod(Module module, MethodDefinitionHandle method) {
            var reader = module.Metadata;
            var definition = reader.GetMethodDefinition(handle: method);
            var text = new StringBuilder();
            var signature = Quiet(name: () => MethodSignature(signature: definition.DecodeSignature(genericContext: null, provider: this)));
            var key = (module.Name + "!" + module.FullName(handle: definition.GetDeclaringType()) + "." + module.NameOf(method: method) + signature);

            m_current = key;
            Reasons[key] = m_from[(module, method)];
            Declares(module: module, type: definition.GetDeclaringType());
            Mention(module: module, type: definition.GetDeclaringType());
            text.Append(value: (int)definition.Attributes).Append(value: ' ').Append(value: (int)definition.ImplAttributes).Append(value: ' ').Append(value: signature);
            if ((definition.RelativeVirtualAddress != 0) && !IsCopy(method: method, module: module, signature: signature)) {
                var body = module.Pe.GetMethodBody(relativeVirtualAddress: definition.RelativeVirtualAddress);

                text.Append(value: "\ninit ").Append(value: body.LocalVariablesInitialized);
                text.Append(value: "\nlocals ").Append(value: (body.LocalSignature.IsNil ? string.Empty : Quiet(name: () => string.Join(separator: ", ", values: reader.GetStandaloneSignature(handle: body.LocalSignature).DecodeLocalSignature(genericContext: null, provider: this)))));
                foreach (var region in body.ExceptionRegions) {
                    text.Append(value: "\ntry ").Append(value: region.Kind).Append(value: ' ').Append(value: region.TryOffset).Append(value: ' ').Append(value: region.TryLength).Append(value: ' ').Append(value: region.HandlerOffset).Append(value: ' ').Append(value: region.HandlerLength).Append(value: ' ').Append(value: region.FilterOffset).Append(value: ' ').Append(value: (region.CatchType.IsNil ? string.Empty : TypeName(handle: region.CatchType, module: module)));
                }
                text.Append(value: '\n');
                Instructions(module: module, reader: body.GetILReader(), text: text);
            }

            m_members[key] = XxHash128.HashToUInt128(source: Encoding.UTF8.GetBytes(s: text.ToString()));
        }
        // The method's instructions, each token operand written as the entity it names and followed into the closure, each
        // other operand as its bytes.
        private void Instructions(Module module, BlobReader reader, StringBuilder text) {
            while (reader.RemainingBytes > 0) {
                int value = reader.ReadByte();
                var code = OpCodesByValue[unchecked((ushort)((value == 0xFE) ? (0xFE00 | reader.ReadByte()) : value))];

                text.Append(value: code.Name).Append(value: ' ');
                if (code.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType) {
                    text.Append(value: Token(code: code, handle: MetadataTokens.Handle(token: reader.ReadInt32()), module: module));
                } else {
                    var bytes = ((code.OperandType == OperandType.InlineSwitch) ? (4 * (int)reader.ReadUInt32()) : OperandBytes[(int)code.OperandType]);

                    text.Append(value: Convert.ToHexString(inArray: reader.ReadBytes(byteCount: bytes)));
                }
                text.Append(value: '\n');
            }
        }
        // A token operand by the entity it names; a Puck method it names joins the closure, and so does what that implies.
        private string Token(Module module, OpCode code, Handle handle) {
            var reader = module.Metadata;

            switch (handle.Kind) {
                case HandleKind.UserString:
                    return ("\"" + reader.GetUserString(handle: (UserStringHandle)handle) + "\"");
                case HandleKind.StandaloneSignature:
                    return Quiet(name: () => MethodSignature(signature: reader.GetStandaloneSignature(handle: (StandaloneSignatureHandle)handle).DecodeMethodSignature(genericContext: null, provider: this)));
                case HandleKind.MethodDefinition:
                case HandleKind.MemberReference when (reader.GetMemberReference(handle: (MemberReferenceHandle)handle).GetKind() == MemberReferenceKind.Method):
                case HandleKind.MethodSpecification: {
                    var (name, target) = MethodToken(handle: (EntityHandle)handle, module: module);

                    if (target is { } resolved) {
                        var definition = resolved.Item1.Metadata.GetMethodDefinition(handle: resolved.Item2);
                        var declaring = definition.GetDeclaringType();

                        Enqueue(method: resolved.Item2, module: resolved.Item1);
                        if (Stopped(module: resolved.Item1, type: declaring)) { return name; }
                        if ((code == OpCodes.Newobj) || resolved.Item1.Metadata.StringComparer.Equals(handle: definition.Name, value: ".ctor")) { Construct(module: resolved.Item1, type: declaring); }
                        if ((definition.Attributes & MethodAttributes.Static) != 0) { Initialize(module: resolved.Item1, type: declaring); }
                    }

                    return name;
                }
                case HandleKind.FieldDefinition:
                case HandleKind.MemberReference: {
                    var (owner, field, fieldModule) = FieldToken(handle: (EntityHandle)handle, module: module);

                    if (field is { } handled) {
                        var resolved = fieldModule!.Metadata.GetFieldDefinition(handle: handled);

                        if (!Stopped(module: fieldModule, type: resolved.GetDeclaringType())) {
                            _ = m_touched.Add(item: (fieldModule, handled));
                            Mention(module: fieldModule, type: resolved.GetDeclaringType());
                            if ((code == OpCodes.Ldsfld) || (code == OpCodes.Ldsflda) || (code == OpCodes.Stsfld)) { Initialize(module: fieldModule, type: resolved.GetDeclaringType()); }
                        }

                        return (owner + FieldData(field: resolved, module: fieldModule));
                    }

                    return owner;
                }
                default:
                    return TypeName(handle: (EntityHandle)handle, module: module);
            }
        }
        // A field token's name and the Puck field definition it resolves to, if any.
        private (string Name, FieldDefinitionHandle? Field, Module? Module) FieldToken(Module module, EntityHandle handle) {
            var reader = module.Metadata;

            if (handle.Kind == HandleKind.FieldDefinition) {
                var field = reader.GetFieldDefinition(handle: (FieldDefinitionHandle)handle);

                return ((TypeName(handle: field.GetDeclaringType(), module: module) + "." + module.NameOf(field: (FieldDefinitionHandle)handle) + " " + Quiet(name: () => field.DecodeSignature(genericContext: null, provider: this))), (FieldDefinitionHandle)handle, module);
            }

            var member = reader.GetMemberReference(handle: (MemberReferenceHandle)handle);
            var parent = Resolve(handle: member.Parent, module: module);
            var fieldName = reader.GetString(handle: member.Name);
            var name = (TypeName(handle: member.Parent, module: module) + "." + ((parent is { } declaring) ? declaring.Item1.Canonical(declaring: declaring.Item2, name: fieldName) : fieldName) + " " + Quiet(name: () => member.DecodeFieldSignature(genericContext: null, provider: this)));
            var match = ((parent is { } owner) ? owner.Item1.Metadata.GetTypeDefinition(handle: owner.Item2).GetFields().Where(predicate: field => owner.Item1.Metadata.StringComparer.Equals(handle: owner.Item1.Metadata.GetFieldDefinition(handle: field).Name, value: reader.GetString(handle: member.Name))).Cast<FieldDefinitionHandle?>().FirstOrDefault() : null);

            return (name, match, parent?.Item1);
        }
        // The data a field initialized from the image holds (an array or span literal), which the IL loads by token: as many
        // bytes as its type holds, or eight for a primitive.
        private static string FieldData(Module module, FieldDefinition field) {
            if ((field.Attributes & FieldAttributes.HasFieldRVA) == 0) { return string.Empty; }

            var blob = module.Metadata.GetBlobReader(handle: field.Signature);
            var data = module.Pe.GetSectionData(relativeVirtualAddress: field.GetRelativeVirtualAddress());

            _ = blob.ReadSignatureHeader();

            var size = (((blob.ReadSignatureTypeCode() == SignatureTypeCode.TypeHandle) && (blob.ReadTypeHandle() is { Kind: HandleKind.TypeDefinition } type)) ? module.Metadata.GetTypeDefinition(handle: (TypeDefinitionHandle)type).GetLayout().Size : sizeof(long));

            return (" = " + Convert.ToHexString(inArray: data.GetContent(start: 0, length: Math.Min(size, data.Length)).ToArray()));
        }
        // A method token's name and the Puck method definition it resolves to, if any. A reference resolves to the method
        // of its name and signature its parent type declares.
        private (string Name, (Module, MethodDefinitionHandle)? Target) MethodToken(Module module, EntityHandle handle) {
            var reader = module.Metadata;

            if (handle.Kind == HandleKind.MethodDefinition) {
                var definition = reader.GetMethodDefinition(handle: (MethodDefinitionHandle)handle);

                return ((TypeName(handle: definition.GetDeclaringType(), module: module) + "." + module.NameOf(method: (MethodDefinitionHandle)handle) + Quiet(name: () => MethodSignature(signature: definition.DecodeSignature(genericContext: null, provider: this)))), (module, (MethodDefinitionHandle)handle));
            }
            if (handle.Kind == HandleKind.MethodSpecification) {
                var specification = reader.GetMethodSpecification(handle: (MethodSpecificationHandle)handle);
                var (generic, target) = MethodToken(handle: specification.Method, module: module);

                return ((generic + "<" + string.Join(separator: ",", values: specification.DecodeSignature(genericContext: null, provider: this)) + ">"), target);
            }

            var member = reader.GetMemberReference(handle: (MemberReferenceHandle)handle);
            var name = reader.GetString(handle: member.Name);
            var signature = Quiet(name: () => MethodSignature(signature: member.DecodeMethodSignature(genericContext: null, provider: this)));
            var parent = Resolve(handle: member.Parent, module: module);
            var match = ((parent is { } owner) ? owner.Item1.Metadata.GetTypeDefinition(handle: owner.Item2).GetMethods().Where(predicate: candidate => owner.Item1.Metadata.StringComparer.Equals(handle: owner.Item1.Metadata.GetMethodDefinition(handle: candidate).Name, value: name) && (Quiet(name: () => MethodSignature(signature: owner.Item1.Metadata.GetMethodDefinition(handle: candidate).DecodeSignature(genericContext: null, provider: this))) == signature)).Select(selector: candidate => ((Module, MethodDefinitionHandle)?)(owner.Item1, candidate)).FirstOrDefault() : null);

            return ((TypeName(handle: member.Parent, module: module) + "." + ((parent is { } declaring) ? declaring.Item1.Canonical(declaring: declaring.Item2, name: name) : name) + signature), match);
        }
        private string Quiet(Func<string> name) {
            var was = m_quiet;

            m_quiet = true;
            try {
                return name();
            } finally {
                m_quiet = was;
            }
        }
        private static string MethodSignature(MethodSignature<string> signature) =>
            ("`" + signature.GenericParameterCount + "(" + string.Join(separator: ",", values: signature.ParameterTypes) + ")" + signature.ReturnType + (signature.Header.IsInstance ? " instance" : string.Empty));
        private string TypeName(Module module, EntityHandle handle) => handle.Kind switch {
            HandleKind.TypeDefinition => GetTypeFromDefinition(handle: (TypeDefinitionHandle)handle, rawTypeKind: 0, reader: module.Metadata),
            HandleKind.TypeReference => GetTypeFromReference(handle: (TypeReferenceHandle)handle, rawTypeKind: 0, reader: module.Metadata),
            _ => GetTypeFromSpecification(genericContext: null, handle: (TypeSpecificationHandle)handle, rawTypeKind: 0, reader: module.Metadata),
        };

        public string GetArrayType(string elementType, ArrayShape shape) => (elementType + "[" + shape.Rank + "]");
        public string GetByReferenceType(string elementType) => (elementType + "&");
        public string GetFunctionPointerType(MethodSignature<string> signature) => ("method" + MethodSignature(signature: signature));
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => (genericType + "<" + string.Join(separator: ",", values: typeArguments) + ">");
        public string GetGenericMethodParameter(object? genericContext, int index) => ("!!" + index);
        public string GetGenericTypeParameter(object? genericContext, int index) => ("!" + index);
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => (unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")");
        public string GetPinnedType(string elementType) => (elementType + " pinned");
        public string GetPointerType(string elementType) => (elementType + "*");
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => (elementType + "[]");
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) {
            var module = readers[reader];

            Mention(module: module, type: handle);

            return module.FullName(handle: handle);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) {
            var module = readers[reader];
            var reference = reader.GetTypeReference(handle: handle);
            var name = reader.GetString(handle: reference.Name);
            var space = reader.GetString(handle: reference.Namespace);

            if (Resolve(handle: handle, module: module) is { } resolved) {
                Mention(module: resolved.Item1, type: resolved.Item2);

                return resolved.Item1.FullName(handle: resolved.Item2);
            }

            return ((reference.ResolutionScope.Kind == HandleKind.TypeReference) ? (GetTypeFromReference(handle: (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind: 0, reader: reader) + "/" + name) : ((space.Length == 0) ? name : (space + "." + name)));
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle: handle).DecodeSignature(genericContext: genericContext, provider: this);
    }
}
