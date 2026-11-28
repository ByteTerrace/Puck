using System.Globalization;
using RuleCostCalibration = Puck.State.Rules.RuleCostCalibration;
using RuleTime = Puck.State.Rules.RuleTime;
using RuleWorkContributor = Puck.State.Rules.RuleWorkContributor;

namespace Puck.World;

/// <summary>One device's reading of a world's rule work sheet in time.</summary>
/// <param name="Device">The device profile's name (<see cref="WorldDeviceProfile.Name"/>), or <c>default</c> for a
/// world that authors no device.</param>
/// <param name="Calibration">The figures the sheet was converted with.</param>
/// <param name="Source">Where the figures came from: the device's own measurement, another device's figures it borrows,
/// or the uncalibrated default.</param>
/// <param name="Tick">The realistic tick admission holds to the budget: what recurs on every tick of play plus the
/// dearest answer one line gives one change (<see cref="Puck.State.Rules.RuleTickPrice.Tick"/>).</param>
/// <param name="Worst">Every line's worst case at once (a boot, every group changing together); reported, never
/// admitted against the budget.</param>
public sealed record WorldRuleDevicePrice(string Device, RuleCostCalibration Calibration, string Source, RuleTime Tick, RuleTime Worst) {
    /// <summary>Gets the whole price the tick was read from: its recurring part, its dearest change, the sheet's total
    /// of one change at every line, and the worst case.</summary>
    public Puck.State.Rules.RuleTickPrice Detail { get; init; }
}
/// <summary>A world's rule work in time: the sheet (<see cref="WorldRuleWorkBudget"/>) converted with every device
/// profile's calibration (<see cref="WorldDeviceProfile.Rules"/>) and compared with the tick's rule budget, the authored
/// share of the tick period (<see cref="WorldSimulationDefaults.RulePercent"/>). The world is admitted against its
/// slowest device, the one that prices the sheet highest, so a document admitted on one machine is admitted on every
/// machine and by every authority: nothing here reads the device it runs on. A device without figures borrows the
/// slowest calibrated device's figures, and a world with none calibrated is priced at
/// <see cref="RuleCostCalibration.Uncalibrated"/>; each says so.</summary>
public sealed class WorldRuleTimePricing {
    private readonly Func<IReadOnlyList<RuleWorkContributor>> m_lines;

    private WorldRuleTimePricing(int rateHz, int rulePercent, long budgetPicoseconds, IReadOnlyList<WorldRuleDevicePrice> devices, string? issue, Func<IReadOnlyList<RuleWorkContributor>> lines) {
        RateHz = rateHz;
        RulePercent = rulePercent;
        BudgetPicoseconds = budgetPicoseconds;
        Devices = devices;
        Issue = issue;
        m_lines = lines;
        Slowest = devices[0];
        foreach (var device in devices) {
            if (RuleTime.Compare(left: device.Tick, right: Slowest.Tick) > 0) { Slowest = device; }
        }
    }

    /// <summary>Gets the tick rate the budget is a share of; a resident world (rate 0) is budgeted at
    /// <see cref="WorldDefinition.UnauthoredSimulationRateHz"/>.</summary>
    public int RateHz { get; }
    /// <summary>Gets the percent of the tick period the rules may take.</summary>
    public int RulePercent { get; }
    /// <summary>Gets the rules' budget per tick, in picoseconds.</summary>
    public long BudgetPicoseconds { get; }
    /// <summary>Gets every device's reading, in the order the world authors its devices.</summary>
    public IReadOnlyList<WorldRuleDevicePrice> Devices { get; }
    /// <summary>Gets the device that prices the sheet highest, which admission holds to the budget.</summary>
    public WorldRuleDevicePrice Slowest { get; }
    /// <summary>Gets why the authored budget or figures cannot be read (a percent out of range, a negative figure), or
    /// <see langword="null"/>.</summary>
    public string? Issue { get; }
    /// <summary>Gets the time the budget leaves after the slowest device's realistic tick, for arena growth and its
    /// rollback; zero when the sheet fills it or has no number.</summary>
    public long GrowthHeadroomPicoseconds => (Slowest.Tick.Fits(budgetPicoseconds: BudgetPicoseconds) ? (BudgetPicoseconds - Slowest.Tick.Picoseconds) : 0L);
    /// <summary>Gets the firing work one tick may charge at run time before it carries its remaining due work to the
    /// next ticks (<see cref="Puck.State.Rules.RuleTickAllowance"/>), in the slowest device's picoseconds: the budget
    /// less the recurring judgements and host passes, which the allowance does not meter. A document constant: it reads
    /// the document's devices and share, never the machine.</summary>
    public long FiringAllowancePicoseconds => (Slowest.Detail.RecurringChecks.Fits(budgetPicoseconds: BudgetPicoseconds)
        ? (BudgetPicoseconds - Slowest.Detail.RecurringChecks.Picoseconds)
        : 0L);

    /// <summary>Prices a sheet for every device a world authors.</summary>
    /// <param name="definition">The world: its rate, its rule share and its device profiles.</param>
    /// <param name="budget">The sheet the world's rules were priced at, in units.</param>
    /// <param name="lines">The sheet's lines, costliest first.</param>
    /// <returns>The pricing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="lines"/> is <see langword="null"/>.</exception>
    public static WorldRuleTimePricing Of(WorldDefinition definition, WorldRuleWorkBudget budget, IReadOnlyList<RuleWorkContributor> lines) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: lines);

        var rate = ((definition.SimulationRateHz > 0) ? definition.SimulationRateHz : WorldDefinition.UnauthoredSimulationRateHz);
        var percent = (definition.Simulation?.RulePercent ?? WorldSimulationDefaults.DefaultRulePercent);
        var issue = (((percent < 1) || (percent > 100)) ? $"simulation.rulePercent is {percent.ToString(provider: CultureInfo.InvariantCulture)}; it is a percent of the tick period, 1 to 100" : null);
        var clamped = Math.Clamp(value: percent, min: 1, max: 100);
        var writers = (budget.Writers ?? new Dictionary<(int RowOrdinal, CellKey Key), Puck.State.Rules.RuleCellWriters>());
        var profiles = (definition.Render.Devices ?? []);
        var measured = new List<WorldRuleDevicePrice>();

        foreach (var profile in profiles) {
            if (profile.Rules is not { } figures) { continue; }
            if (figures.Refusal() is { } refusal) {
                issue ??= $"render.devices '{profile.Name}' rules: {refusal}";
                continue;
            }
            measured.Add(item: Read(calibration: figures, device: profile.Name, source: ((figures.Build is { } build) ? $"measured: {build}" : "authored")));
        }

        // The slowest calibrated device's figures stand for a device that has none, so missing hardware never opens
        // more room than the hardware on hand measured.
        var borrowed = measured.Count switch {
            0 => null,
            _ => measured.Aggregate(func: static (slowest, next) => ((RuleTime.Compare(left: next.Tick, right: slowest.Tick) > 0) ? next : slowest)),
        };
        var devices = new List<WorldRuleDevicePrice>(capacity: Math.Max(val1: 1, val2: profiles.Count));

        foreach (var profile in profiles) {
            var own = measured.Find(match: price => string.Equals(a: price.Device, b: profile.Name, comparisonType: StringComparison.Ordinal));

            devices.Add(item: own ?? ((borrowed is null)
                ? Read(calibration: RuleCostCalibration.Uncalibrated, device: profile.Name, source: "uncalibrated: no device of the world is calibrated, so the engine's default figures price it")
                : Read(calibration: borrowed.Calibration, device: profile.Name, source: $"uncalibrated: priced at '{borrowed.Device}', the slowest calibrated device")));
        }
        if (devices.Count == 0) {
            devices.Add(item: Read(calibration: RuleCostCalibration.Uncalibrated, device: "default", source: "uncalibrated: the world authors no device, so the engine's default figures price it"));
        }

        return new WorldRuleTimePricing(
            budgetPicoseconds: (long)((((Int128)1_000_000_000_000L) * clamped) / (100 * rate)),
            devices: devices,
            issue: issue,
            lines: () => lines,
            rateHz: rate,
            rulePercent: clamped
        );

        WorldRuleDevicePrice Read(RuleCostCalibration calibration, string device, string source) {
            var price = calibration.Price(hostUnits: budget.HostWorkUnitsPerTick, lines: lines, writers: writers);

            return new WorldRuleDevicePrice(Calibration: calibration, Device: device, Source: source, Tick: price.Tick, Worst: price.Worst) { Detail = price };
        }
    }

    /// <summary>Returns the reading of one device the world authors.</summary>
    /// <param name="device">The device profile's name.</param>
    /// <returns>The reading, or <see langword="null"/> when the world authors no such device.</returns>
    public WorldRuleDevicePrice? For(string device) => Devices.FirstOrDefault(predicate: price => string.Equals(a: price.Device, b: device, comparisonType: StringComparison.Ordinal));
    /// <summary>Returns admission's refusal of the sheet, or <see langword="null"/> when the slowest device's realistic tick
    /// fits the budget. A bound nothing prices or that overflowed has no number and is refused, never read as small.</summary>
    /// <param name="rowsAtCeiling">Names, for one line, the rows priced at the engine's cell ceiling for want of a
    /// capacity (<see cref="WorldRuleWorkBudget.RowsAtCeiling(Puck.State.Rules.RuleCompileContext, Puck.State.Rules.CompiledRule)"/>),
    /// or <see langword="null"/> to name none; called only to word a refusal.</param>
    /// <returns>The refusal, in the author's vocabulary, or <see langword="null"/>.</returns>
    public string? Refusal(Func<string, IEnumerable<string>>? rowsAtCeiling = null) {
        if (Issue is { } issue) { return issue; }
        if (Slowest.Tick.Fits(budgetPicoseconds: BudgetPicoseconds)) { return null; }

        var calibration = Slowest.Calibration;
        var costliest = m_lines()
            .Select(selector: line => (Line: line, Time: calibration.Time(line: line).WorkUnits))
            .OrderByDescending(keySelector: static entry => entry.Time, comparer: Comparer<RuleWork>.Create(comparison: RuleWork.Compare))
            .Take(count: 3)
            .ToArray();
        var named = string.Join(separator: ", ", values: costliest.Select(selector: static entry => $"'{entry.Line.Name}' x{entry.Line.Multiplier} = {(entry.Time.IsKnown ? RuleTime.Milliseconds(picoseconds: entry.Time.Units) : entry.Time.ToString())}"));
        var uncapped = ((rowsAtCeiling is null) ? [] : costliest.SelectMany(selector: entry => rowsAtCeiling(arg: entry.Line.Name)).Distinct(comparer: StringComparer.Ordinal).ToArray());
        var fix = ((uncapped.Length == 0)
            ? "a forEach line's multiplier is its row's capacity, so author the capacity the row needs"
            : $"{string.Join(separator: ", ", values: uncapped.Select(selector: static name => $"'{name}'"))} declare no capacity and are priced at the {StateCapacity.MaxCellsPerRow}-cell ceiling they may grow to: author each one's capacity, or key it over a capped token domain");

        return $"rules/interactions/flock affinities are priced at {(Slowest.Tick.IsKnown
            ? $"{Slowest.Tick} a tick on '{Slowest.Device}' ({Slowest.Source}), past"
            : $"no bound on their time a tick on '{Slowest.Device}' ({Slowest.Tick}), which cannot be admitted under")} the rules' {RuleTime.Milliseconds(picoseconds: BudgetPicoseconds)}, {RulePercent.ToString(provider: CultureInfo.InvariantCulture)}% of the {RateHz.ToString(provider: CultureInfo.InvariantCulture)} Hz tick; costliest: {named} ({fix}; world.budget.rules lists every line and how it is priced).";
    }
    /// <summary>Returns the refusal of an arena growth while a tick runs, or <see langword="null"/> when the growth and
    /// its rollback fit what the budget leaves after the slowest device's realistic tick, priced at that device's growth
    /// figure. The same document refuses the same growth on every machine.</summary>
    /// <param name="growthUnits">The growth units already spent this tick plus the proposed change and its rollback
    /// (<see cref="StateArena.ReservationWork"/>).</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? RefuseGrowth(long growthUnits) {
        var growth = Slowest.Calibration.GrowthTime(units: growthUnits);

        if (growth.IsKnown && Slowest.Tick.IsKnown && (growth + Slowest.Tick).Fits(budgetPicoseconds: BudgetPicoseconds)) { return null; }
        return $"arena growth and rollback ({growthUnits.ToString(provider: CultureInfo.InvariantCulture)} units, {growth} on '{Slowest.Device}') plus the rules' realistic tick ({Slowest.Tick}) exceed the rules' {RuleTime.Milliseconds(picoseconds: BudgetPicoseconds)} a tick";
    }
    /// <summary>Spells the budget and every device's reading, one line each, for <c>world.budget.rules</c> and
    /// <c>puck compile</c>.</summary>
    /// <param name="prefix">The text each line begins with.</param>
    /// <returns>The lines, the budget first.</returns>
    public IReadOnlyList<string> Describe(string prefix) {
        var output = new List<string>(capacity: (Devices.Count + 1)) {
            $"{prefix} time: realistic tick {Slowest.Tick} on '{Slowest.Device}' of {RuleTime.Milliseconds(picoseconds: BudgetPicoseconds)} ({RulePercent.ToString(provider: CultureInfo.InvariantCulture)}% of the {RateHz.ToString(provider: CultureInfo.InvariantCulture)} Hz tick); growth headroom {RuleTime.Milliseconds(picoseconds: GrowthHeadroomPicoseconds)}; firing allowance {RuleTime.Milliseconds(picoseconds: FiringAllowancePicoseconds)}; {((Refusal() is null) ? "admitted" : "refused")}{((Issue is { } issue) ? $": {issue}" : string.Empty)}",
        };

        foreach (var device in Devices) {
            output.Add(item: $"{prefix} device {device.Device}: tick={device.Tick} (recurring {device.Detail.Recurring} + dearest change {device.Detail.DearestChange}{((device.Detail.DearestChangeLine.Length > 0) ? $" '{device.Detail.DearestChangeLine}'" : string.Empty)}) one-change-per-line={device.Detail.Changes} worst={device.Worst} {device.Calibration.Describe()} ({device.Source})");
        }

        return output;
    }
}
