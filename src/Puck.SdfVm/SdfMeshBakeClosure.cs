using System.Collections.Immutable;
using System.IO.Hashing;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Puck.SdfVm;

/// <summary>
/// The C# a mesh bake executes, read from a build's assemblies: every method the bake's own code reaches through calls,
/// delegates, the overrides of the types it constructs and the implementations of the abstract members it calls, across
/// the <c>Puck</c> assemblies, each hashed by its IL with every token written as the member it names rather than its
/// number, with its locals, its exception regions and the field data it loads; and the layout of every type that code
/// names. Its <see cref="Hash"/> is the code half of the bake's identity (<see cref="SdfMeshBakeIdentity.Code"/>): an
/// edit anywhere else (the rest of the renderer, a new public member of a referenced assembly, a comment, the checkout's
/// path) leaves it as it was, and an edit to anything the bake runs changes it.
/// <para>The bake's roots (<see cref="BakeRoots"/>) are its planning, sampling, contouring and meshing types, the
/// frame-block rows its kernel reads, the program's packing of the words the kernel walks, and the store's entry format.
/// The walk stops at the library and the store, which hold meshes rather than make them; at the kernels' interfaces and
/// the render graph's package, whose declarations reach the bake through the kernels' bytecode; and at assemblies outside
/// <c>Puck</c>: the runtime is the platform the bake runs on, as the driver is for its kernels. A type a parameter, a
/// local or a field merely carries joins the closure only where code makes, converts or calls into it, and a record's
/// synthesized printing and equality follow its layout. Code the bake reaches only through reflection, or through a
/// runtime callback into a type it never constructs, is outside the walk and needs a root. Immutable.</para>
/// </summary>
public sealed partial class SdfMeshBakeClosure {
    /// <summary>The roots of the bake's closure: each an assembly, a type's full name, and one method of it by name (every
    /// overload), or <see langword="null"/> for every method of the type and of the types nested in it.</summary>
    public static readonly IReadOnlyList<(string Assembly, string Type, string? Method)> BakeRoots = [
        ("Puck.SdfVm", "Puck.SdfVm.SdfMesh", null),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBatch", null),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeGroup", null),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeRequest", null),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshGeometry", null),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshGeometryBuilder", null),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshMaterialCut", null),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshPathBake", null),
        ("Puck.SdfVm", "Puck.SdfVm.SdfFrameBlock", "WriteMeshBake"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshStore", "Encode"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshStore", "Refusal"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshStore", "TryDecode"),
        // The program packs the words the bake kernel walks; a group's geometry hashes its instructions, not their packing.
        ("Puck.SignedDistance", "Puck.SignedDistance.SdfProgram", ".ctor"),
    ];
    private static readonly HashSet<string> StopTypes = new(collection: [
        "Puck.SdfVm.SdfKernelInterfaces",
        "Puck.SdfVm.SdfMeshBakeClosure",
        "Puck.SdfVm.SdfMeshBakeIdentity",
        "Puck.SdfVm.SdfMeshLibrary",
        "Puck.SdfVm.SdfMeshStore",
        "Puck.SdfVm.SdfWorldInterfaces",
        "Puck.Shaders.SdfWorldPackage",
    ], comparer: StringComparer.Ordinal);
    private static readonly Lazy<SdfMeshBakeClosure> Running = new(valueFactory: static () => Of(directory: AppContext.BaseDirectory));

    private SdfMeshBakeClosure(SortedDictionary<string, UInt128> members, SortedDictionary<string, string> reasons) {
        var hash = new XxHash128();
        Span<byte> value = stackalloc byte[16];

        foreach (var (member, memberHash) in members) {
            hash.Append(source: Encoding.UTF8.GetBytes(s: member));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt128LittleEndian(destination: value, value: memberHash);
            hash.Append(source: value);
        }

        Hash = hash.GetCurrentHashAsUInt128();
        Members = members;
        Reasons = reasons;
    }

    /// <summary>Gets the bake's closure in the build this process runs, read from its application directory
    /// (<see cref="AppContext.BaseDirectory"/>) once.</summary>
    public static SdfMeshBakeClosure Current => Running.Value;
    /// <summary>Gets the hash of every member: the code half of the bake's identity.</summary>
    public UInt128 Hash { get; }
    /// <summary>Gets each member of the closure (a method, or a type's layout, as
    /// <c>assembly!type.member`generics(parameters)return</c>) with the hash of what it executes, in ordinal order.</summary>
    public IReadOnlyDictionary<string, UInt128> Members { get; }
    /// <summary>Gets, for each member, the member the walk first reached it from, or <c>root</c> for a root: following
    /// it back says why a member is in the closure.</summary>
    public IReadOnlyDictionary<string, string> Reasons { get; }

    /// <summary>Reads the bake's closure (<see cref="BakeRoots"/>) in the build deployed in a directory from its
    /// assemblies' metadata and IL, without loading them.</summary>
    /// <param name="directory">The directory the build's assemblies are deployed in.</param>
    /// <returns>The closure.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    /// <exception cref="FileNotFoundException">The directory holds no root's assembly.</exception>
    /// <exception cref="BadImageFormatException">An assembly carries no readable metadata.</exception>
    public static SdfMeshBakeClosure Of(string directory) => Of(directory: directory, roots: BakeRoots);
    /// <summary>Reads the closure of other roots in the build deployed in a directory, as <see cref="Of(string)"/> reads
    /// the bake's: a tool measuring what some code runs reads it this way.</summary>
    /// <param name="directory">The directory the build's assemblies are deployed in.</param>
    /// <param name="roots">The roots, as <see cref="BakeRoots"/> spells them.</param>
    /// <returns>The closure.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="roots"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The directory holds no root's assembly.</exception>
    /// <exception cref="KeyNotFoundException">A root names a type its assembly does not define.</exception>
    /// <exception cref="BadImageFormatException">An assembly carries no readable metadata.</exception>
    public static SdfMeshBakeClosure Of(string directory, IReadOnlyList<(string Assembly, string Type, string? Method)> roots) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentNullException.ThrowIfNull(argument: roots);

        var walk = new Walk(directory: Path.GetFullPath(path: directory));

        return new SdfMeshBakeClosure(members: walk.Run(roots: roots), reasons: walk.Reasons);
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

    // One assembly the walk reads: its metadata, its image and its types by full name.
    private sealed class Module {
        public Module(string name, byte[] image) {
            Name = name;
            Pe = new PEReader(peImage: ImmutableArray.Create(items: image));
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
            var name = FileLocalChecksum().Replace(input: Metadata.GetString(handle: type.Name), replacement: "__");
            var declaring = type.GetDeclaringType();
            var space = Metadata.GetString(handle: type.Namespace);

            return ((!declaring.IsNil) ? (FullName(handle: declaring) + "/" + name) : ((space.Length == 0) ? name : (space + "." + name)));
        }
    }

    // The walk over one build's assemblies, from the roots to every member they reach.
    private sealed class Walk(string directory) : ISignatureTypeProvider<string, object?> {
        private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(bindingAttr: (BindingFlags.Public | BindingFlags.Static))
            .Select(selector: static field => (OpCode)field.GetValue(obj: null)!)
            .ToDictionary(keySelector: static code => unchecked((ushort)code.Value));
        // The bytes each operand type takes after its opcode, indexed by OperandType's value (InlineBrTarget 0, InlineField,
        // InlineI, InlineI8, InlineMethod, InlineNone, InlinePhi, InlineR 7, 8 unused, InlineSig 9, InlineString,
        // InlineSwitch, InlineTok, InlineType, InlineVar 14, ShortInlineBrTarget, ShortInlineI, ShortInlineR,
        // ShortInlineVar 18); a token or a switch's targets are read apart.
        private static readonly int[] OperandBytes = [4, 4, 4, 8, 4, 0, 0, 8, 0, 4, 4, 0, 4, 4, 2, 1, 1, 4, 1];

        private readonly Dictionary<string, Module?> m_modules = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<MetadataReader, Module> m_byReader = [];
        private readonly SortedDictionary<string, UInt128> m_members = new(comparer: StringComparer.Ordinal);
        private readonly HashSet<(Module, MethodDefinitionHandle)> m_methodsSeen = [];
        private readonly Queue<(Module, MethodDefinitionHandle)> m_methods = new();
        private readonly HashSet<(Module, TypeDefinitionHandle)> m_typesSeen = [];
        private readonly Queue<(Module, TypeDefinitionHandle)> m_types = new();
        private readonly HashSet<(Module, TypeDefinitionHandle)> m_constructed = [];
        // The fields the closure's code loads, stores or takes the address of, and the types whose layout counts only those.
        private readonly HashSet<(Module, FieldDefinitionHandle)> m_touched = [];
        private readonly List<(Module, TypeDefinitionHandle)> m_byTouch = [];
        // Each type of the indexed assemblies by the types it derives from or implements directly.
        private readonly Dictionary<(Module, TypeDefinitionHandle), List<(Module, TypeDefinitionHandle)>> m_derived = [];
        // The member each entity was first reached from, and the member being hashed, which reaches whatever it names.
        private readonly Dictionary<(Module, EntityHandle), string> m_from = [];
        private string m_current = "root";
        // While a layout or a signature is named, the types it names join nothing.
        private bool m_quiet;

        public SortedDictionary<string, string> Reasons { get; } = new(comparer: StringComparer.Ordinal);

        public SortedDictionary<string, UInt128> Run(IReadOnlyList<(string Assembly, string Type, string? Method)> roots) {
            foreach (var (assembly, type, method) in roots) {
                var module = (Load(name: assembly) ?? throw new FileNotFoundException(message: $"'{directory}' holds no {assembly}.dll.", fileName: Path.Combine(path1: directory, path2: (assembly + ".dll"))));

                Index(root: module);
                Root(method: method, module: module, type: module.Types[type]);
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

            foreach (var handle in definition.GetMethods()) {
                if ((method is null) || module.Metadata.StringComparer.Equals(handle: module.Metadata.GetMethodDefinition(handle: handle).Name, value: method)) { Enqueue(method: handle, module: module, root: true); }
            }
            foreach (var nested in ((method is null) ? definition.GetNestedTypes() : [])) {
                Root(method: null, module: module, type: nested);
            }
        }
        private Module? Load(string name) {
            if (m_modules.TryGetValue(key: name, value: out var held)) { return held; }

            var path = Path.Combine(path1: directory, path2: (name + ".dll"));
            var module = ((name.StartsWith(value: "Puck.", comparisonType: StringComparison.Ordinal) && File.Exists(path: path)) ? new Module(image: File.ReadAllBytes(path: path), name: name) : null);

            m_modules[name] = module;
            if (module is not null) { m_byReader[module.Metadata] = module; }

            return module;
        }
        // Loads every Puck assembly a root's references, transitively, and indexes each type under the types it derives
        // from and implements, so a call to an abstract member reaches every implementation those assemblies hold.
        private void Index(Module root) {
            var pending = new Queue<Module>(collection: [root]);
            var indexed = new HashSet<Module>();

            while (pending.TryDequeue(result: out var module)) {
                if (!indexed.Add(item: module) || m_indexed.Contains(item: module)) { continue; }
                foreach (var handle in module.Metadata.AssemblyReferences) {
                    if (Load(name: module.Metadata.GetString(handle: module.Metadata.GetAssemblyReference(handle: handle).Name)) is { } referenced) { pending.Enqueue(item: referenced); }
                }
            }
            foreach (var module in indexed.Where(predicate: m_indexed.Add).OrderBy(keySelector: static module => module.Name, comparer: StringComparer.Ordinal)) {
                foreach (var handle in module.Metadata.TypeDefinitions) {
                    var type = module.Metadata.GetTypeDefinition(handle: handle);

                    foreach (var parent in type.GetInterfaceImplementations().Select(selector: implementation => module.Metadata.GetInterfaceImplementation(handle: implementation).Interface).Prepend(element: type.BaseType)) {
                        if (!parent.IsNil && (Resolve(handle: parent, module: module) is { } resolved)) {
                            if (!m_derived.TryGetValue(key: resolved, value: out var list)) { m_derived[resolved] = list = []; }
                            list.Add(item: (module, handle));
                        }
                    }
                }
            }
        }
        private readonly HashSet<Module> m_indexed = [];
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
                var target = ((outer is { } declaring) ? declaring.Item1 : ((scope.Kind == HandleKind.AssemblyReference) ? Load(name: reader.GetString(handle: reader.GetAssemblyReference(handle: (AssemblyReferenceHandle)scope).Name)) : null));
                var full = ((outer is { } within) ? (within.Item1.FullName(handle: within.Item2) + "/" + name) : ((space.Length == 0) ? name : (space + "." + name)));

                return (((target is not null) && target.Types.TryGetValue(key: full, value: out var definition)) ? (target, definition) : null);
            }

            // A type specification: a generic instantiation names its definition next; any other names no Puck type.
            var blob = reader.GetBlobReader(handle: reader.GetTypeSpecification(handle: (TypeSpecificationHandle)handle).Signature);

            return ((blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance) && (blob.ReadSignatureTypeCode() is SignatureTypeCode.TypeHandle) ? Resolve(handle: blob.ReadTypeHandle(), module: module) : null);
        }
        private static bool Stopped(Module module, TypeDefinitionHandle type) {
            var name = module.FullName(handle: type);
            var outer = name.IndexOf(value: '/', comparisonType: StringComparison.Ordinal);

            return StopTypes.Contains(item: ((outer < 0) ? name : name[..outer]));
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
        // behavior its hashed layout decides.
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
        // A call to a virtual or abstract member of a Puck type reaches the members of its name in every type deriving from
        // or implementing it.
        private void Dispatch(Module module, MethodDefinitionHandle method) {
            var definition = module.Metadata.GetMethodDefinition(handle: method);
            var name = module.Metadata.GetString(handle: definition.Name);
            var pending = new Queue<(Module, TypeDefinitionHandle)>(collection: (((definition.Attributes & MethodAttributes.Virtual) != 0) ? [(module, definition.GetDeclaringType())] : []));
            var seen = new HashSet<(Module, TypeDefinitionHandle)>();

            while (pending.TryDequeue(result: out var type)) {
                foreach (var (derivedModule, derivedType) in ((seen.Add(item: type) && m_derived.TryGetValue(key: type, value: out var derived)) ? derived : [])) {
                    pending.Enqueue(item: (derivedModule, derivedType));
                    foreach (var candidate in derivedModule.Metadata.GetTypeDefinition(handle: derivedType).GetMethods()) {
                        var candidateName = derivedModule.Metadata.GetString(handle: derivedModule.Metadata.GetMethodDefinition(handle: candidate).Name);

                        if ((candidateName == name) || candidateName.EndsWith(value: ("." + name), comparisonType: StringComparison.Ordinal)) { Enqueue(method: candidate, module: derivedModule); }
                    }
                }
            }
        }

        // A value type's layout is the bytes its code moves, so it counts whole; any other type's layout counts the fields
        // the closure's code reads or writes (HashLayout, once the walk ends and every such field is known), and a field the
        // code never touches cannot change what it does.
        private void HashType(Module module, TypeDefinitionHandle type) {
            var definition = module.Metadata.GetTypeDefinition(handle: type);
            var baseName = (definition.BaseType.IsNil ? string.Empty : Quiet(name: () => TypeName(handle: definition.BaseType, module: module)));

            m_current = (module.Name + "!" + module.FullName(handle: type) + " [layout]");
            Reasons[m_current] = m_from[(module, type)];
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

                text.Append(value: '\n').Append(value: (int)field.Attributes).Append(value: ' ').Append(value: reader.GetString(handle: field.Name)).Append(value: ' ').Append(value: Quiet(name: () => field.DecodeSignature(genericContext: null, provider: this))).Append(value: ' ').Append(value: field.GetOffset());
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
            var key = (module.Name + "!" + module.FullName(handle: definition.GetDeclaringType()) + "." + reader.GetString(handle: definition.Name) + signature);

            m_current = key;
            Reasons[key] = m_from[(module, method)];
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
                        if ((code == OpCodes.Callvirt) || (code == OpCodes.Ldvirtftn)) { Dispatch(method: resolved.Item2, module: resolved.Item1); }
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

                return ((TypeName(handle: field.GetDeclaringType(), module: module) + "." + reader.GetString(handle: field.Name) + " " + Quiet(name: () => field.DecodeSignature(genericContext: null, provider: this))), (FieldDefinitionHandle)handle, module);
            }

            var member = reader.GetMemberReference(handle: (MemberReferenceHandle)handle);
            var name = (TypeName(handle: member.Parent, module: module) + "." + reader.GetString(handle: member.Name) + " " + Quiet(name: () => member.DecodeFieldSignature(genericContext: null, provider: this)));
            var parent = Resolve(handle: member.Parent, module: module);
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

                return ((TypeName(handle: definition.GetDeclaringType(), module: module) + "." + reader.GetString(handle: definition.Name) + Quiet(name: () => MethodSignature(signature: definition.DecodeSignature(genericContext: null, provider: this)))), (module, (MethodDefinitionHandle)handle));
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

            return ((TypeName(handle: member.Parent, module: module) + "." + name + signature), match);
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
            var module = m_byReader[reader];

            Mention(module: module, type: handle);

            return module.FullName(handle: handle);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) {
            var module = m_byReader[reader];
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
