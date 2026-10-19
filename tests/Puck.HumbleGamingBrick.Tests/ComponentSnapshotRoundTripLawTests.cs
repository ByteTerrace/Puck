using System.Reflection;
using Puck.Machines;

namespace Puck.HumbleGamingBrick.Tests;

/// <summary>
/// Every Humble component restores exactly what it saves. Each case seeds every mutable scalar field and every
/// scalar-array element of a live component with a value derived from the field's own name and saves it. No layout or
/// digest is recorded anywhere.
/// <para>The saved bytes then load into a twin seeded with different values and must save back unchanged, with the reader
/// consumed exactly — a field the save writes but the load leaves alone keeps the twin's value and fails the case.</para>
/// </summary>
public sealed class ComponentSnapshotRoundTripLawTests {
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const string PrimarySalt = "layout";
    private const string TwinSalt = "twin";

    // A field whose restore validates its value gets a seed inside the range that value may hold.
    private static readonly Dictionary<string, Func<ulong, object>> ConstrainedFields = new(comparer: StringComparer.Ordinal) {
        ["HdmaController.m_state"] = static seed => ((byte)(seed % 4)),
    };

    /// <summary>Gets every case: each component a standard machine registers, each mapper, and the printer.</summary>
    public static TheoryData<string> Cases {
        get {
            var cases = new TheoryData<string>();

            foreach (var name in CaseFactories.Keys.Order(comparer: StringComparer.Ordinal)) {
                cases.Add(row: name);
            }

            return cases;
        }
    }

    private static readonly Dictionary<string, Func<Case>> CaseFactories = BuildCaseFactories();

    private static Dictionary<string, Func<Case>> BuildCaseFactories() {
        var factories = new Dictionary<string, Func<Case>>(comparer: StringComparer.Ordinal);

        using (var probe = BuildMachine()) {
            foreach (var component in probe.GetRequiredService<IEnumerable<ISnapshotable>>()) {
                var name = component.GetType().Name;

                factories.Add(
                    key: name,
                    value: () => {
                        var machine = BuildMachine();

                        return new Case(
                            Component: machine.GetRequiredService<IEnumerable<ISnapshotable>>().Single(predicate: candidate => (candidate.GetType().Name == name)),
                            Owner: machine
                        );
                    }
                );
            }
        }

        foreach (var (cartridgeType, ramSize) in new (byte CartridgeType, byte RamSize)[] {
            (0x00, 0x00),
            (0x03, 0x02),
            (0x06, 0x00),
            (0x13, 0x02),
            (0x1E, 0x02),
            (0x22, 0x00),
            (0x0D, 0x02),
            (0xFF, 0x02),
            (0xFE, 0x02),
            (0xFC, 0x04),
        }) {
            var name = Cartridge.Load(rom: CreateRom(
                cartridgeType: cartridgeType,
                ramSize: ramSize
            )).GetType().Name;

            factories.Add(
                key: name,
                value: () => new Case(
                    Component: ((ISnapshotable)Cartridge.Load(rom: CreateRom(
                        cartridgeType: cartridgeType,
                        ramSize: ramSize
                    ))),
                    Owner: null
                )
            );
        }

        factories.Add(
            key: nameof(GamePrinterDevice),
            value: static () => new Case(
                Component: new GamePrinterDevice(),
                Owner: null
            )
        );

        return factories;
    }
    private static MachineInstance<Machine, MachineConfiguration> BuildMachine() => MachineFactory.Create(configuration: new MachineConfiguration(
        cartridgeRom: CreateRom(
            cartridgeType: 0x00,
            ramSize: 0x00
        ),
        model: ConsoleModel.CgbD
    ));
    private static byte[] CreateRom(byte cartridgeType, byte ramSize) {
        var rom = new byte[0x8000];

        rom[0x0147] = cartridgeType;
        rom[0x0149] = ramSize;

        return rom;
    }
    private static ulong Hash(string key) {
        var hash = FnvOffsetBasis;

        foreach (var character in key) {
            hash = ((hash ^ character) * FnvPrime);
        }

        return hash;
    }
    private static bool IsScalar(Type type) => (
        (type == typeof(bool)) ||
        (type == typeof(byte)) ||
        (type == typeof(sbyte)) ||
        (type == typeof(short)) ||
        (type == typeof(ushort)) ||
        (type == typeof(int)) ||
        (type == typeof(uint)) ||
        (type == typeof(long)) ||
        (type == typeof(ulong)) ||
        (type.IsEnum && IsScalar(type: Enum.GetUnderlyingType(enumType: type)))
    );
    private static byte[] Save(ISnapshotable component) {
        var writer = new StateWriter();

        component.SaveState(writer: writer);

        return writer.ToArray();
    }
    private static object ScalarValue(Type type, ulong seed) {
        if (type.IsEnum) {
            return Enum.ToObject(
                enumType: type,
                value: ScalarValue(
                    seed: seed,
                    type: Enum.GetUnderlyingType(enumType: type)
                )
            );
        }

        return Type.GetTypeCode(type: type) switch {
            TypeCode.Boolean => ((seed & 1UL) != 0UL),
            TypeCode.Byte => ((byte)seed),
            TypeCode.SByte => ((sbyte)seed),
            TypeCode.Int16 => ((short)seed),
            TypeCode.UInt16 => ((ushort)seed),
            TypeCode.Int32 => ((int)seed),
            TypeCode.UInt32 => ((uint)seed),
            TypeCode.Int64 => ((long)seed),
            TypeCode.UInt64 => seed,
            _ => throw new NotSupportedException(message: $"{type} is not a scalar field type."),
        };
    }
    private static void SeedFields(object component, string salt) {
        for (var type = component.GetType(); ((type is not null) && (type != typeof(object))); type = type.BaseType) {
            foreach (var field in type.GetFields(bindingAttr: BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                var key = $"{type.Name}.{field.Name}";

                if (
                    field.FieldType.IsArray &&
                    (field.FieldType.GetArrayRank() == 1) &&
                    IsScalar(type: field.FieldType.GetElementType()!)
                ) {
                    if (field.GetValue(obj: component) is not Array array) {
                        continue;
                    }

                    var elementType = field.FieldType.GetElementType()!;

                    for (var index = 0; (index < array.Length); ++index) {
                        array.SetValue(
                            index: index,
                            value: ScalarValue(
                                seed: Hash(key: $"{salt}:{key}[{index}]"),
                                type: elementType
                            )
                        );
                    }
                } else if (
                    IsScalar(type: field.FieldType) &&
                    !field.IsInitOnly
                ) {
                    var seed = Hash(key: $"{salt}:{key}");

                    field.SetValue(
                        obj: component,
                        value: (ConstrainedFields.TryGetValue(
                            key: key,
                            value: out var constrain
                        )
                            ? constrain(arg: seed)
                            : ScalarValue(
                                seed: seed,
                                type: field.FieldType
                            )
                        )
                    );
                }
            }
        }
    }

    [MemberData(memberName: nameof(Cases))]
    [Theory]
    public void SavedState_RestoresIntoATwinExactly(string name) {
        var primary = CaseFactories[name]();
        var twin = CaseFactories[name]();

        try {
            SeedFields(
                component: primary.Component,
                salt: PrimarySalt
            );

            var bytes = Save(component: primary.Component);

            SeedFields(
                component: twin.Component,
                salt: TwinSalt
            );

            var reader = new StateReader(buffer: bytes);

            twin.Component.LoadState(reader: reader);

            Assert.True(
                condition: reader.AtEnd,
                userMessage: $"{name}'s load left bytes unread"
            );
            Assert.Equal(
                expected: bytes,
                actual: Save(component: twin.Component)
            );
        } finally {
            primary.Owner?.Dispose();
            twin.Owner?.Dispose();
        }
    }

    private sealed record Case(ISnapshotable Component, IDisposable? Owner);
}
