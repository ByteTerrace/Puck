using System.Reflection;
using System.Text;
using Puck.Machines;

namespace Puck.AdvancedGamingBrick.Tests;

/// <summary>
/// Every Advanced component restores exactly what it saves. Each case seeds every mutable scalar field and every
/// scalar-array element of a live component with a value derived from the field's own path and saves it, descending
/// into the component's own value-type units (a length counter, an envelope, the sample-phase accumulator) and into the
/// parts it owns outright (the APU's channels and Direct Sound FIFOs). No layout or digest is recorded anywhere.
/// <para>The saved bytes then load into a twin seeded with different values and must save back unchanged, with the reader
/// consumed exactly — a field the save writes but the load leaves alone keeps the twin's value and fails the case.</para>
/// </summary>
public sealed class ComponentSnapshotRoundTripLawTests {
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const string PrimarySalt = "layout";
    private const string TwinSalt = "twin";

    // A field whose restore indexes a table by its value gets a seed inside that table.
    private static readonly Dictionary<string, Func<ulong, object>> ConstrainedFields = new(comparer: StringComparer.Ordinal) {
        ["AgbTimerController.m_frequency"] = static seed => ((int)(seed % 4)),
    };
    // Reference-typed parts a component owns outright and snapshots inside its own bytes; seeding descends into them.
    // Any other reference (the scheduler, a scheduled event, a sibling component) is wiring, not the component's state.
    private static readonly HashSet<string> OwnedPartTypes = new(comparer: StringComparer.Ordinal) {
        "ApuNoiseChannel",
        "ApuPulseChannel",
        "ApuWaveChannel",
        "DirectSoundFifo",
    };

    /// <summary>Gets every case: each component the machine snapshots, and each APU channel on its own.</summary>
    public static TheoryData<string> Cases {
        get {
            var cases = new TheoryData<string>();

            foreach (var name in CaseFactories.Keys.Order(comparer: StringComparer.Ordinal)) {
                cases.Add(row: name);
            }

            return cases;
        }
    }

    private static readonly Dictionary<string, Func<MachineInstance<AdvancedGamingBrickMachine, AgbMachineConfiguration>, ISnapshotable>> CaseFactories = new(comparer: StringComparer.Ordinal) {
        ["AgbApu"] = static machine => Snapshotable(component: machine.GetRequiredService<IAgbApu>()),
        ["AgbBus"] = static machine => Snapshotable(component: machine.GetRequiredService<IAgbBus>()),
        ["AgbCartridge"] = static machine => Snapshotable(component: machine.GetRequiredService<AgbCartridge>()),
        ["AgbDmaController"] = static machine => Snapshotable(component: machine.GetRequiredService<IAgbDmaController>()),
        ["AgbInterruptController"] = static machine => Snapshotable(component: machine.GetRequiredService<IAgbInterruptController>()),
        ["AgbPpu"] = static machine => Snapshotable(component: machine.GetRequiredService<IAgbPpu>()),
        ["AgbScheduler"] = static machine => Snapshotable(component: machine.GetRequiredService<AgbScheduler>()),
        ["AgbSerialController"] = static machine => Snapshotable(component: machine.GetRequiredService<IAgbSerialController>()),
        ["AgbTimerController"] = static machine => Snapshotable(component: machine.GetRequiredService<IAgbTimerController>()),
        ["ApuNoiseChannel"] = static machine => ApuPart(
            field: "m_noise",
            machine: machine
        ),
        ["ApuPulseChannel"] = static machine => ApuPart(
            field: "m_pulse1",
            machine: machine
        ),
        ["ApuWaveChannel"] = static machine => ApuPart(
            field: "m_wave",
            machine: machine
        ),
        ["Arm7Tdmi"] = static machine => Snapshotable(component: machine.GetRequiredService<IArmCpu>()),
    };

    private static ISnapshotable ApuPart(MachineInstance<AdvancedGamingBrickMachine, AgbMachineConfiguration> machine, string field) {
        var apu = machine.GetRequiredService<IAgbApu>();

        return Snapshotable(component: apu.GetType().GetField(
            bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic,
            name: field
        )!.GetValue(obj: apu)!);
    }
    private static MachineInstance<AdvancedGamingBrickMachine, AgbMachineConfiguration> BuildMachine() {
        // A zeroed ROM that names the SRAM library, so the cartridge carries a populated save image.
        var rom = new byte[0x10000];

        Encoding.ASCII.GetBytes(s: "SRAM_V113").CopyTo(array: rom, index: 0x1000);

        return AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(
            bios: new byte[ReplacementBios.ImageSize],
            rom: rom
        ));
    }
    private static ulong Hash(string key) {
        var hash = FnvOffsetBasis;

        foreach (var character in key) {
            hash = ((hash ^ character) * FnvPrime);
        }

        return hash;
    }
    private static bool IsOwnState(Type type) => (
        (type.Assembly == typeof(AdvancedGamingBrickMachine).Assembly) ||
        (type.Assembly == typeof(ISnapshotable).Assembly)
    );
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
    private static object Seed(string key, ulong seed, Type type) =>
        (ConstrainedFields.TryGetValue(
            key: key,
            value: out var constrain
        )
            ? constrain(arg: seed)
            : ScalarValue(
                seed: seed,
                type: type
            ));
    // Seeds every field of one object (or boxed struct), keyed by its path from the component: "{type}.{field}" at the
    // top, and "{owner path}/{type}.{field}" inside an owned part or a value-type unit.
    private static void SeedFields(object target, string prefix, string salt) {
        for (var type = target.GetType(); ((type is not null) && (type != typeof(object)) && (type != typeof(ValueType))); type = type.BaseType) {
            foreach (var field in type.GetFields(bindingAttr: BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                var key = $"{prefix}{type.Name}.{field.Name}";
                var fieldType = field.FieldType;

                if (
                    fieldType.IsArray &&
                    (fieldType.GetArrayRank() == 1) &&
                    IsScalar(type: fieldType.GetElementType()!)
                ) {
                    if (field.GetValue(obj: target) is not Array array) {
                        continue;
                    }

                    var elementType = fieldType.GetElementType()!;

                    for (var index = 0; (index < array.Length); ++index) {
                        array.SetValue(
                            index: index,
                            value: Seed(
                                key: key,
                                seed: Hash(key: $"{salt}:{key}[{index}]"),
                                type: elementType
                            )
                        );
                    }
                } else if (field.IsInitOnly && !OwnedPartTypes.Contains(item: fieldType.Name)) {
                    continue;
                } else if (IsScalar(type: fieldType)) {
                    field.SetValue(
                        obj: target,
                        value: Seed(
                            key: key,
                            seed: Hash(key: $"{salt}:{key}"),
                            type: fieldType
                        )
                    );
                } else if (
                    fieldType.IsValueType &&
                    !fieldType.IsPrimitive &&
                    IsOwnState(type: fieldType)
                ) {
                    var unit = field.GetValue(obj: target)!;

                    SeedFields(
                        prefix: $"{key}/",
                        salt: salt,
                        target: unit
                    );
                    field.SetValue(
                        obj: target,
                        value: unit
                    );
                } else if (
                    !fieldType.IsValueType &&
                    OwnedPartTypes.Contains(item: fieldType.Name) &&
                    (field.GetValue(obj: target) is { } part)
                ) {
                    SeedFields(
                        prefix: $"{key}/",
                        salt: salt,
                        target: part
                    );
                }
            }
        }
    }
    private static ISnapshotable Snapshotable(object component) => ((ISnapshotable)component);

    [MemberData(memberName: nameof(Cases))]
    [Theory]
    public void SavedState_RestoresIntoATwinExactly(string name) {
        using var primaryMachine = BuildMachine();
        using var twinMachine = BuildMachine();
        var primary = CaseFactories[name](arg: primaryMachine);
        var twin = CaseFactories[name](arg: twinMachine);

        SeedFields(
            prefix: string.Empty,
            salt: PrimarySalt,
            target: primary
        );

        var bytes = Save(component: primary);


        SeedFields(
            prefix: string.Empty,
            salt: TwinSalt,
            target: twin
        );

        var reader = new StateReader(buffer: bytes);

        twin.LoadState(reader: reader);

        Assert.True(
            condition: reader.AtEnd,
            userMessage: $"{name}'s load left bytes unread"
        );
        Assert.Equal(
            expected: bytes,
            actual: Save(component: twin)
        );
    }
}
