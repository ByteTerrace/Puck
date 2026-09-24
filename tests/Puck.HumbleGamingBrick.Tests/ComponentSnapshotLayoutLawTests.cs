using System.Reflection;
using System.Security.Cryptography;
using Puck.GamingBricks;

namespace Puck.HumbleGamingBrick.Tests;

/// <summary>
/// Pins every Humble component's snapshot byte layout. Each case seeds every mutable scalar field and every scalar-array
/// element of a live component with a value derived from the field's own name, saves it, and compares the bytes'
/// length and SHA-256 with the value recorded for that component. Because each value is keyed by name rather than by
/// declaration or serialization order, the recorded bytes pin which field lands at which offset: a reordered, retyped,
/// dropped, or added field changes the digest even when save and load still agree with each other.
/// <para>The same bytes then load into a twin seeded with different values and must save back unchanged, with the reader
/// consumed exactly — a field the save writes but the load leaves alone keeps the twin's value and fails the case.</para>
/// </summary>
public sealed class ComponentSnapshotLayoutLawTests {
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const string PrimarySalt = "layout";
    private const string TwinSalt = "twin";

    // Each value is "{byte length}:{SHA-256}" of the component's seeded save. Snapshot bytes are the state-of-record
    // determinism surface, so changing a value is a snapshot format change and bumps MachineIdentity.CurrentVersion in
    // the same change.
    private static readonly Dictionary<string, string> RecordedLayouts = new(comparer: StringComparer.Ordinal) {
        ["ApuComponent"] = "250:D3F34CAFA03CDCB37F539D8D3D997732A3C9422355F66435B1814B5D1ADC1EC8",
        ["AudioOutputComponent"] = "0:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
        ["CameraCartridge"] = "131140:AF4336726EDD71339736EBDAEDA5CABAADE210DD8E4E8AAB9714FA86C933A945",
        ["CartridgeSlot"] = "0:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
        ["DmgCompatibilityState"] = "2:96A296D224F285C67BEE93C30F8A309157F0DAA35DC5B87E410B78630A09CFC7",
        ["Framebuffer"] = "92160:44B5FB953CA835E495AA0C0822F806F8BDF232E04FD8F8E17ED57DE2526E1882",
        ["GamePrinterDevice"] = "32669:BB4E79DFE442E0865977DEF204DFE30C1254F523E90B14EC1ECA69A40A033380",
        ["HdmaController"] = "26:B673A17F4FA7792F8302C8C140FEBB062463D92FDAF9DD31FFC394B7FBCB6F5F",
        ["HuC1Cartridge"] = "8201:259E62F91F70656EA28F26DF7F01FA087A591BE2315E13D41F481FB8CBBF4E55",
        ["HuC3Cartridge"] = "8228:D2C21D5D88A6CD3B5E15D2C29ABC3988B45299D5887BBC50E617B0F0F6FF45B5",
        ["InfraredPort"] = "2:E9619023F7AD0507CCC2F44D1FF58550018171C87F6D6F95C393CB2CA9EAB3BA",
        ["InterruptController"] = "2:BD13566E6FFCAE4C554AC27CF00F511DCC5036439760C865B13FDDF42DAEABDF",
        ["JoypadComponent"] = "3:E71B6DEA74213517BBA82749660272241790CA4242BEA810370CB35B66040C67",
        ["Key1Component"] = "26:1157DA6A1043C75715217994D69C923D3589E442E87C08B00BCD8D90C5B06442",
        ["Mbc1Cartridge"] = "8202:7055F9B882E71C6ED326FEC519CFA5041120E7C9305C41D3D5746C48A67D3576",
        ["Mbc2Cartridge"] = "517:5D7D85D6C0400DF7151ABBD1310CC86B49BE55CA816EE15B350B762C4001D4FA",
        ["Mbc3Cartridge"] = "8247:ABCF61DD120704E217D34F41724CA900815CADD74FEFF306D685A9D86B9933A9",
        ["Mbc5Cartridge"] = "8202:6F9FC50CC9801141AC068ED0A5718ACF23E14BD6448D09CDDCB19767E25F2A97",
        ["Mbc7Cartridge"] = "288:2F1213A1FA077813153F38F96D08BA249070FCDC9F8DA069DAFBDC22A003C1BB",
        ["Mmm01Cartridge"] = "8225:B5B410AC1342F3C383B0DCCE3EDE6516DF879602387F3CBEFE55F2F6E071EDBF",
        ["ModelState"] = "1:8C2574892063F995FDF756BCE07F46C1A5193E54CD52837ED91E32008CCF41AC",
        ["OamDmaController"] = "24:01E4E12809E37A1FCB5D0D6767BFBBB68C89E7D069744DC28284BCFFE62A1017",
        ["Ppu"] = "337:8ABF98517945E0C979CBB4EA46B5FC93D8262F96A5088C7C3B631FEEEE42E01E",
        ["RomOnlyCartridge"] = "0:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
        ["SerialComponent"] = "7:7C2E5D7EEC9A0AF8ED520AFA6691F2EFCFD4FFEE2222AAA48FCF23C10D02E59F",
        ["Sm83"] = "20:F23D6921216C8244C742B974A7EE78C8133A6F19A5EAEDCF4E9BBE13F527C65B",
        ["SystemBus"] = "129:32726E0393DABB99FB7F86D9FB4769A5173CC043C31409733B7A2250B5EDF8E1",
        ["SystemMemory"] = "49447:766BAC12B4C4D3527187B3FFFA00BD68CFCBC546B12C1EFB487FBEBF815397FC",
        ["TiltSensorComponent"] = "8:3EDDFCAB22FD9F71810D351EA563F6A6A331E90B7AAE2926E694AA1D1B656D0E",
        ["TimerComponent"] = "13:357CEBAD84E71A9194453B87522DB19AE0661607BFE27187C005A95022537991",
    };
    // A field whose restore validates its value gets a seed inside the range that value may hold.
    private static readonly Dictionary<string, Func<ulong, object>> ConstrainedFields = new(comparer: StringComparer.Ordinal) {
        ["HdmaController.m_state"] = static seed => ((byte)(seed % 4)),
    };

    /// <summary>Gets every pinned case: each component a standard machine registers, each mapper, and the printer.</summary>
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
    private static string Describe(byte[] bytes) => $"{bytes.Length}:{Convert.ToHexString(inArray: SHA256.HashData(source: bytes))}";
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
    public void SavedBytes_MatchTheRecordedLayout_AndRestoreIntoATwinExactly(string name) {
        var primary = CaseFactories[name]();
        var twin = CaseFactories[name]();

        try {
            SeedFields(
                component: primary.Component,
                salt: PrimarySalt
            );

            var bytes = Save(component: primary.Component);

            Assert.True(
                condition: RecordedLayouts.TryGetValue(
                    key: name,
                    value: out var recorded
                ),
                userMessage: $"{name} has no recorded layout; its bytes describe as {Describe(bytes: bytes)}"
            );
            Assert.Equal(
                expected: recorded,
                actual: Describe(bytes: bytes)
            );

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
