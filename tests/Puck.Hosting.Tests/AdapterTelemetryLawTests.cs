using Puck.Hosting.Telemetry;

namespace Puck.Hosting.Tests;

public sealed class AdapterTelemetryLawTests {
    private static readonly AdapterTelemetryInfo Adapter = new(Provider: "test", Adapter: "Test adapter", MaxGraphicsClockMhz: 3000, MaxMemoryClockMhz: 10000, PowerLimitWatts: 200, Unavailable: null);

    private static AdapterTelemetryReading[] Readings(int count, double clock, double utilization, AdapterClockEvents events = AdapterClockEvents.None) => [.. Enumerable.Repeat(
        element: new AdapterTelemetryReading(ClockEvents: events, GraphicsClockMhz: clock, MemoryClockMhz: 9000, MemoryUtilizationPercent: 30, PerformanceState: 0, PowerWatts: 150, TemperatureCelsius: 60, UtilizationPercent: utilization),
        count: count)];

    [Fact]
    public void ALoadedAdapterNearItsCeilingIsNotHeld() {
        var summary = AdapterTelemetrySummary.Of(info: Adapter, readings: Readings(count: 10, clock: 2850, utilization: 95));

        Assert.Null(summary.HeldReason);
        Assert.Null(summary.NotJudged);
    }

    [Fact]
    public void ALoadedAdapterAtTwoThirdsOfItsCeilingWithNoLimitIsHeld() {
        var summary = AdapterTelemetrySummary.Of(info: Adapter, readings: Readings(count: 10, clock: 1920, utilization: 95, events: AdapterClockEvents.ApplicationClocks));

        Assert.NotNull(summary.HeldReason);
        Assert.Contains(expectedSubstring: "1920", actualString: summary.HeldReason);
    }

    [Theory]
    [InlineData(AdapterClockEvents.PowerCap)]
    [InlineData(AdapterClockEvents.ThermalSlowdown)]
    [InlineData(AdapterClockEvents.HardwareSlowdown)]
    public void APowerOrThermalLimitExplainsALowClock(AdapterClockEvents events) {
        var summary = AdapterTelemetrySummary.Of(info: Adapter, readings: Readings(count: 10, clock: 1920, utilization: 95, events: events));

        Assert.Null(summary.HeldReason);
        Assert.Null(summary.NotJudged);
    }

    [Fact]
    public void AnIdleAdapterIsNotJudged() {
        var summary = AdapterTelemetrySummary.Of(info: Adapter, readings: Readings(count: 10, clock: 300, utilization: 5));

        Assert.Null(summary.HeldReason);
        Assert.NotNull(summary.NotJudged);
    }

    [Fact]
    public void AnUnavailableProviderIsNeverJudgedOrAFailure() {
        using var telemetry = AdapterTelemetry.Open(adapter: "no such adapter anywhere");
        var summary = AdapterTelemetrySummary.Of(info: telemetry.Info, readings: [telemetry.Read()]);

        Assert.Null(summary.HeldReason);
    }
}
