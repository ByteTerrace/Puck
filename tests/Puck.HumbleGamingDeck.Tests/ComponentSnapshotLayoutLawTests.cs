using System.Reflection;
using System.Security.Cryptography;
using Puck.Machines;

namespace Puck.HumbleGamingDeck.Tests;

/// <summary>Pins each component's named-field byte layout independently of save/load symmetry.</summary>
public sealed class ComponentSnapshotLayoutLawTests {
    private static readonly Dictionary<string, string> RecordedLayouts = new(comparer: StringComparer.Ordinal) {
        ["HgdCpu`1"] = "41:3B5992441A767490A4886FC46FCE8385E456D735CA01BB47F01B73064989649B",
        ["HgdSystemBus"] = "2049:2F3FB2C2903AAA036679257FEDBB99E6A41E6CC37D544D838ADE62ECE84D72A4",
        ["HgdNrom"] = "16384:D0AF537F0022EE61BF38F019FCC229C0ACF114DADEC7A5382B8100F0CA1AF8F8",
        ["HgdClock"] = "24:BAF90ED330310EC9191BA4BD1052A81690FBCA9F2CADD4B66F50C640815EBD61",
        ["HgdPpu"] = "246226:035D8E53633CA27D412404EF55FB82F14C104081CF88AEA947FCDD372CF40D19",
        ["HgdApu"] = "85:B7EE236D74A12064B474890CE8B7F9E071010348FE07F3DA12EF695D3AD72139",
        ["HgdDma"] = "12:CEAF2E2C474CFF7E2E26ED75BC6F45577251FED0220CA75AC4A50EED4D97B102",
        ["HgdControllers"] = "5:C1E5F9070476F5E4F1965CF1B3887179A2D9386DE612AD89B976F8C0BC37A1AD",
        ["HgdNametableRam"] = "2048:119B441214B6343E70F6F1A4DCD16F78817BF4DB9240760056389EBE54995479",
    };

    /// <summary>Gets the components whose complete state layouts are pinned.</summary>
    public static TheoryData<string> Cases => ["HgdClock", "HgdCpu`1", "HgdSystemBus", "HgdNrom", "HgdPpu", "HgdApu", "HgdDma", "HgdControllers", "HgdNametableRam"];

    // Fields whose loaders refuse values outside a range, seeded inside it.
    private static readonly Dictionary<string, int> FieldRanges = new(comparer: StringComparer.Ordinal) {
        ["m_ppuPhase"] = 4,
        ["m_spriteCount"] = 9,
        ["m_nextSpriteCount"] = 9,
        ["m_spritesFound"] = 9,
        ["m_copyRemaining"] = 4,
        ["m_oamIndex"] = 257,
    };

    /// <summary>Verifies the named-field layout and a load into independently seeded state.</summary>
    /// <param name="name">The component type name.</param>
    [MemberData(memberName: nameof(Cases))]
    [Theory]
    public void SavedBytesMatchRecordedLayoutAndRestoreIntoDifferentlySeededTwin(string name) {
        var primary = Component(name: name);
        var twin = Component(name: name);

        Seed(component: primary, salt: "layout");
        Seed(component: twin, salt: "twin");
        var writer = new StateWriter();

        primary.SaveState(writer: writer);
        var bytes = writer.ToArray();
        var description = $"{bytes.Length}:{Convert.ToHexString(inArray: SHA256.HashData(source: bytes))}";

        Assert.True(condition: RecordedLayouts.TryGetValue(key: name, value: out var recorded), userMessage: $"Unpinned {name}: {description}");
        Assert.Equal(actual: description, expected: recorded);
        var reader = new StateReader(buffer: bytes);

        twin.LoadState(reader: reader);
        Assert.True(condition: reader.AtEnd);
        writer.Reset();
        twin.SaveState(writer: writer);
        Assert.Equal(expected: bytes, actual: writer.ToArray());
    }

    private static ISnapshotable Component(string name) {
        var image = new byte[(16 + 16384)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = 1;
        image[7] = 8;
        image[10] = 7;
        image[11] = 7;
        var machine = new HgdMachine(configuration: new HgdMachineConfiguration(cartridge: HgdCartridge.Load(image: image)));

        return name switch {
            "HgdClock" => machine.Clock,
            "HgdCpu`1" => machine.Cpu,
            "HgdSystemBus" => machine.Bus,
            "HgdNrom" => machine.Bus.Mapper,
            "HgdPpu" => machine.Ppu,
            "HgdApu" => machine.Apu,
            "HgdDma" => machine.Dma,
            "HgdControllers" => machine.Controllers,
            "HgdNametableRam" => machine.Nametables,
            _ => throw new ArgumentException(message: name),
        };
    }
    private static ulong Hash(string key) {
        var value = 14695981039346656037UL;

        foreach (var character in key) {
            value = unchecked(((value ^ character) * 1099511628211UL));
        }

        return value;
    }
    private static void Seed(object component, string salt) {
        var type = component.GetType();

        foreach (var field in type.GetFields(bindingAttr: BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            var key = $"{salt}:{type.Name}.{field.Name}";

            if ((field.FieldType == typeof(byte[])) && (field.GetValue(obj: component) is byte[] bytes)) {
                for (var index = 0; (index < bytes.Length); ++index) {
                    bytes[index] = ((byte)Hash(key: $"{key}[{index}]"));
                }
            } else if ((field.FieldType == typeof(ushort[])) && (field.GetValue(obj: component) is ushort[] words)) {
                for (var index = 0; (index < words.Length); ++index) {
                    words[index] = ((ushort)Hash(key: $"{key}[{index}]"));
                }
            } else if (!field.IsInitOnly) {
                var seed = Hash(key: key);
                object? value = ((field.FieldType == typeof(byte)) ? (byte)seed
                    : ((field.FieldType == typeof(ushort)) ? (ushort)seed
                    : ((field.FieldType == typeof(ulong)) ? ((field.Name == "m_runTargetCycles") ? seed & 0xFFFFUL : seed)
                    : ((field.FieldType == typeof(long)) ? ((long)(seed & 0xFFFFFFFFUL))
                    : ((field.FieldType == typeof(int)) ? (int)(seed % ((ulong)FieldRanges.GetValueOrDefault(key: field.Name, defaultValue: 12)))
                    : ((field.FieldType == typeof(bool)) ? ((seed & 1) != 0) : null))))));

                if (field.FieldType.IsEnum) {
                    var values = Enum.GetValues(enumType: field.FieldType);

                    value = values.GetValue(index: ((int)(seed % ((ulong)values.Length))));
                }
                if (value is not null) {
                    field.SetValue(obj: component, value: value);
                }
            }
        }
    }
}
