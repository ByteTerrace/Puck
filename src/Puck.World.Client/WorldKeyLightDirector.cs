using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Abstractions.Lighting;
using Puck.Commands;
using Puck.Input.Lighting;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// Presentation-side keyboard lamp director: each fixed step it builds every local seat's legend from its routed
/// world's <c>keyLights</c> section (the keys the rows name, the keys the seat's active binding page binds, and the keys
/// that type the characters state holds), paints it through <see cref="LightLegendComposer"/>, and writes the frame to
/// the platform's keyboard lamp arrays. It reads delivered state only and never feeds simulation.
/// </summary>
/// <remarks>
/// A seat's legend is always painted onto a reference board holding one lamp per key
/// <see cref="KeyboardUsageMap"/> maps, so <c>lights.state</c> reads the composed colors on a host with no lamp array
/// (headless, or one whose devices are off). The physical arrays show the legend of the lowest local seat that holds a
/// keyboard, or of the lowest seat whose world authors the section when none does: the director publishes it to the
/// <see cref="LampLegendDriver"/>, whose own thread takes the arrays and writes them, and withdraws it when no seat's
/// world authors one.
/// </remarks>
public sealed class WorldKeyLightDirector {
    private readonly LampLegendDriver? m_driver;
    private readonly ReferenceBoard m_board = new();
    private readonly PlayerRoster m_roster;
    private readonly WorldSeatBindings m_seatBindings;
    private readonly SeatSlot[] m_seats;
    private readonly Func<long> m_timestamp;
    private readonly List<string> m_sourceScratch = [];

    private long m_lastTickTimestamp;
    private int m_owner = -1;

    /// <summary>Initializes a new instance of the <see cref="WorldKeyLightDirector"/> class.</summary>
    /// <param name="roster">The local seat roster, which names the seat that holds the keyboard.</param>
    /// <param name="seatBindings">The routed definition, state mirror and active binding page of each seat.</param>
    /// <param name="driver">The loop that writes the platform's lamp arrays, or <see langword="null"/> on a host that
    /// opens no physical device; the director does not own it.</param>
    /// <param name="timestamp">The monotonic timestamp source, in <see cref="Stopwatch.Frequency"/> ticks, or
    /// <see langword="null"/> for <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="roster"/> or <paramref name="seatBindings"/> is
    /// <see langword="null"/>.</exception>
    public WorldKeyLightDirector(PlayerRoster roster, WorldSeatBindings seatBindings, LampLegendDriver? driver = null, Func<long>? timestamp = null) {
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: seatBindings);

        m_roster = roster;
        m_seatBindings = seatBindings;
        m_driver = driver;
        m_timestamp = (timestamp ?? Stopwatch.GetTimestamp);
        m_seats = new SeatSlot[PlayerRoster.MaxSlots];
        for (var slot = 0; (slot < m_seats.Length); slot++) {
            m_seats[slot] = new SeatSlot();
        }
        m_lastTickTimestamp = m_timestamp();
    }

    /// <summary>Paints every local seat's legend for the completed tick and publishes the keyboard owner's to the
    /// lamp arrays' loop.</summary>
    public void Tick() {
        var now = m_timestamp();
        var elapsedSeconds = ((float)Math.Max(val1: 0d, val2: (((double)(now - m_lastTickTimestamp)) / Stopwatch.Frequency)));

        m_lastTickTimestamp = now;

        var owner = -1;

        for (var slot = 0; (slot < m_seats.Length); slot++) {
            var seat = m_seats[slot];

            ComposeSeat(elapsedSeconds: elapsedSeconds, seat: seat, slot: slot);
            if (
                seat.Authored &&
                ((owner < 0) || (!m_seats[owner].HoldsKeyboard && seat.HoldsKeyboard))
            ) {
                owner = slot;
            }
        }

        m_owner = owner;
        if (m_driver is null) {
            return;
        }
        if (owner < 0) {
            m_driver.Withdraw();
        } else {
            m_driver.Publish(
                flashSeconds: m_seats[owner].FlashSeconds,
                legend: m_seats[owner].Legend,
                palette: m_seats[owner].Palette!
            );
        }
    }
    /// <summary>Formats the <c>lights.state</c> read-back: each seat whose world authors <c>keyLights</c>, the keys its
    /// legend lights with the colors the composer painted, and the lamp arrays being written.</summary>
    /// <returns>The read-back line.</returns>
    public string Describe() {
        var builder = new StringBuilder(value: "[lights.state:");
        var any = false;

        for (var slot = 0; (slot < m_seats.Length); slot++) {
            var seat = m_seats[slot];

            if (!seat.Authored) {
                continue;
            }

            any = true;
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" seat{PlayerRoster.DisplayNumber(slot: slot)} rows={seat.RowCount} lit={seat.LitCount} flashes={seat.Flashes} keys="
            );

            var wrote = false;

            for (var lamp = 0; (lamp < m_board.LampCount); lamp++) {
                var color = seat.BoardColors[lamp];

                if (color == seat.Idle) {
                    continue;
                }

                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{(wrote ? "," : string.Empty)}{m_board.SourceOf(lamp: lamp)}:#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
                );
                wrote = true;
            }

            if (!wrote) {
                _ = builder.Append(value: "none");
            }
        }

        if (!any) {
            _ = builder.Append(value: " no seat's world authors keyLights");
        }

        var status = (m_driver?.Status ?? default);

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" | arrays={((m_driver is null) ? "off" : status.Arrays.ToString(provider: CultureInfo.InvariantCulture))} taken={status.Taken} frames={status.FramesWritten} owner={((m_owner >= 0) ? $"seat{PlayerRoster.DisplayNumber(slot: m_owner)}" : "none")}]"
        );

        return builder.ToString();
    }
    private void ComposeSeat(SeatSlot seat, int slot, float elapsedSeconds) {
        m_seatBindings.GetRoutedState(
            definition: out var definition,
            slot: slot,
            state: out var mirror
        );

        if (
            !m_roster.IsJoined(slot: slot) ||
            (mirror is null) ||
            (definition.KeyLightsRaw is not { } section)
        ) {
            seat.Authored = false;
            seat.Trackers.Clear();

            return;
        }

        if (!ReferenceEquals(objA: seat.SeenMirror, objB: mirror) || (seat.SeenLifetime != mirror.Lifetime)) {
            seat.SeenMirror = mirror;
            seat.SeenLifetime = mirror.Lifetime;
            seat.Trackers.Clear();
        }

        var idle = ToLamp(color: mirror.Color(color: section.Idle, delivered: true, fallback: Vector4.Zero));
        var flash = ToLamp(color: mirror.Color(color: section.Flash, delivered: true, fallback: Vector4.One));
        var modifier = ToLamp(color: mirror.Color(color: section.Modifier, delivered: true, fallback: Vector4.One));

        if (
            (seat.Composer is null) ||
            (seat.Idle != idle) ||
            (seat.Palette!.Flash != flash) ||
            (seat.Palette.Modifier != modifier) ||
            (seat.Palette.UnavailableScale != section.UnavailableScale)
        ) {
            seat.Palette = LightLegendPalette.Create(
                flash: flash,
                idle: idle,
                modifier: modifier,
                unavailableScale: section.UnavailableScale
            );
            seat.Composer = new LightLegendComposer(palette: seat.Palette);
            seat.Idle = idle;
        }

        seat.Authored = true;
        seat.FlashSeconds = section.FlashSeconds;
        seat.HoldsKeyboard = m_roster.TryGetSeatDevice(
            device: out _,
            kind: InputDeviceKind.Keyboard,
            slot: slot
        );
        seat.Legend.Clear();

        var page = m_seatBindings.PageView(slot: slot);

        foreach (var held in page.Modifiers) {
            if (!held.Required) {
                continue;
            }
            foreach (var source in held.Sources) {
                if (IsKeyboard(source: source)) {
                    seat.Legend.HoldModifier(source: source);
                }
            }
        }

        var seatNumber = PlayerRoster.DisplayNumber(slot: slot);
        var rows = (section.Rows ?? []);
        var matching = 0;
        var lit = 0;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            if (
                (row.Seat is { } token) &&
                (!PrincipalTokens.TryParseSeat(number: out var target, token: token) || (target != seatNumber))
            ) {
                continue;
            }

            matching++;

            var flashes = false;

            if (row.FlashSignal is { } signalScalar) {
                var signal = mirror.Scalar(delivered: true, fallback: 0f, scalar: in signalScalar);

                if (seat.Trackers.TryGetValue(key: row.Id, value: out var last)) {
                    flashes = (signal != last);
                }
                seat.Trackers[row.Id] = signal;
            }

            var paints = ((row.Lit is not { } litScalar) || (mirror.Scalar(delivered: true, fallback: 0f, scalar: in litScalar) > 0f));

            if (!paints && !flashes) {
                continue;
            }

            m_sourceScratch.Clear();
            foreach (var key in (row.Keys ?? [])) {
                m_sourceScratch.Add(item: key);
            }
            foreach (var entry in (row.Bound ?? [])) {
                foreach (var button in page.Buttons) {
                    if (!string.Equals(a: button.Id, b: entry, comparisonType: StringComparison.Ordinal)) {
                        continue;
                    }
                    foreach (var source in button.Sources) {
                        if (IsKeyboard(source: source)) {
                            m_sourceScratch.Add(item: source);
                        }
                    }
                }
            }
            if (
                (row.Character is { } characterScalar) &&
                KeyboardUsageMap.TryGetSource(
                    scalar: ((int)mirror.Scalar(delivered: true, fallback: 0f, scalar: in characterScalar)),
                    source: out var typed
                )
            ) {
                m_sourceScratch.Add(item: typed);
            }

            if (paints) {
                var color = ToLamp(color: mirror.Color(color: row.Color, delivered: true, fallback: Vector4.One));
                var available = ((row.Available is not { } availableScalar) || (mirror.Scalar(delivered: true, fallback: 0f, scalar: in availableScalar) > 0f));

                foreach (var source in m_sourceScratch) {
                    seat.Legend.Bind(color: color, isAvailable: available, source: source);
                }
                lit += m_sourceScratch.Count;
            }
            if (flashes) {
                LampColor? flashColor = ((row.FlashColor is { } authored)
                    ? ToLamp(color: mirror.Color(color: authored, delivered: true, fallback: Vector4.One))
                    : null);

                foreach (var source in m_sourceScratch) {
                    seat.Legend.Flash(color: flashColor, source: source);
                }
                seat.Flashes++;
            }
        }

        seat.RowCount = matching;
        seat.LitCount = lit;
        seat.Composer!.Compose(
            destination: seat.BoardColors,
            device: m_board,
            flashDecay: FlashDecay(elapsedSeconds: elapsedSeconds, flashSeconds: section.FlashSeconds),
            state: seat.Legend
        );
    }
    private static float FlashDecay(float elapsedSeconds, float flashSeconds) =>
        ((flashSeconds > 0f) ? (elapsedSeconds / flashSeconds) : 1f);
    private static bool IsKeyboard(string source) => source.StartsWith(
        comparisonType: StringComparison.OrdinalIgnoreCase,
        value: "keyboard."
    );
    private static LampColor ToLamp(Vector4 color) => new(
        Blue: Channel(value: color.Z),
        Green: Channel(value: color.Y),
        Intensity: 255,
        Red: Channel(value: color.X)
    );
    private static byte Channel(float value) => ((byte)MathF.Round(x: (Math.Clamp(max: 1f, min: 0f, value: value) * 255f)));

    private sealed class SeatSlot {
        public readonly LightLegendState Legend = new();
        public readonly Dictionary<string, float> Trackers = new(comparer: StringComparer.Ordinal);
        public readonly LampColor[] BoardColors = new LampColor[ReferenceBoard.Usages.Length];
        public bool Authored;
        public LightLegendComposer? Composer;
        public ulong Flashes;
        public float FlashSeconds;
        public bool HoldsKeyboard;
        public LampColor Idle;
        public int LitCount;
        public LightLegendPalette? Palette;
        public int RowCount;
        public int SeenLifetime = -1;
        public WorldStateMirror? SeenMirror;
    }
    // One lamp per keyboard usage the usage map resolves, so the composer paints a seat's legend with no array present.
    private sealed class ReferenceBoard : ILampArrayDevice {
        public static readonly ushort[] Usages = BuildUsages();

        private readonly string[] m_sources;

        public ReferenceBoard() {
            m_sources = new string[Usages.Length];
            for (var lamp = 0; (lamp < Usages.Length); lamp++) {
                _ = KeyboardUsageMap.TryGetSource(
                    source: out m_sources[lamp],
                    usage: Usages[lamp],
                    usagePage: KeyboardUsageMap.KeyboardUsagePage
                );
            }
        }

        public string DeviceId => "reference";
        public LampArrayKind Kind => LampArrayKind.Keyboard;
        public int LampCount => Usages.Length;
        public int MinUpdateIntervalInMilliseconds => 0;

        public string SourceOf(int lamp) => m_sources[lamp];
        public bool TryGetLampInfo(int index, out LampInfo info) {
            if (((uint)index) >= ((uint)Usages.Length)) {
                info = default;

                return false;
            }

            info = new LampInfo(
                BlueLevelCount: 255,
                GreenLevelCount: 255,
                InputBindingUsage: Usages[index],
                InputBindingUsagePage: KeyboardUsageMap.KeyboardUsagePage,
                IntensityLevelCount: 255,
                IsProgrammable: true,
                LampId: index,
                Position: default,
                Purposes: LampPurposes.Control,
                RedLevelCount: 255,
                UpdateLatencyInMilliseconds: 0
            );

            return true;
        }
        public bool TrySetAutonomousMode(bool enabled) => true;
        public void UpdateAllLamps(LampColor color) { }
        public void UpdateLamps(ReadOnlySpan<int> lampIds, ReadOnlySpan<LampColor> colors) { }
        public void Dispose() { }

        private static ushort[] BuildUsages() {
            var usages = new List<ushort>();

            for (var usage = 0; (usage <= 0xE7); usage++) {
                if (KeyboardUsageMap.TryGetSource(
                    source: out _,
                    usage: ((ushort)usage),
                    usagePage: KeyboardUsageMap.KeyboardUsagePage
                )) {
                    usages.Add(item: ((ushort)usage));
                }
            }

            return [.. usages];
        }
    }
}
