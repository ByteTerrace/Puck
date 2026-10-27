using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldTestHostLawTests {
    [Fact]
    public void DevelopmentRejectsAnUnusedSingletonWithACaptiveScopedDependency() {
        var builder = Builder(environment: "Development");

        builder.Services.AddScoped<ScopedValue>();
        builder.Services.AddSingleton<CaptiveSingleton>();

        var exception = Assert.Throws<AggregateException>(testCode: () => new WorldTestHost().Build(builder: builder));

        Assert.Contains("Cannot consume scoped service", exception.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public void CallerOwnedSingletonSurvivesHostDisposalAndRemainsShared() {
        var worker = new WorldTestHost();
        var supplied = new CallerOwned();
        var writer = Builder();

        writer.Services.AddSingleton(implementationInstance: supplied);
        using (var host = worker.Build(builder: writer)) {
            host.Services.GetRequiredService<CallerOwned>().Value = 77;
        }
        var reader = Builder();

        reader.Services.AddSingleton(implementationInstance: supplied);
        using (var host = worker.Build(builder: reader)) {
            Assert.Same(supplied, host.Services.GetRequiredService<CallerOwned>());
            Assert.Equal(77, supplied.Value);
        }
        Assert.Equal(0, supplied.Disposals);
    }
    [Fact]
    public void ChildScopesKeepHostSingletonsAndEnvironmentButIsolateScopedServices() {
        var worker = new WorldTestHost();
        SingletonValue? previous = null;

        for (var run = 0; (run < 2); run++) {
            var builder = Builder();

            builder.Services.AddSingleton<SingletonValue>();
            builder.Services.AddScoped<ScopedValue>();
            using var host = worker.Build(builder: builder);
            var singleton = host.Services.GetRequiredService<SingletonValue>();
            using var first = host.Services.CreateScope();
            using var second = first.ServiceProvider.CreateScope();

            Assert.NotSame(actual: singleton, expected: previous);
            Assert.Same(singleton, second.ServiceProvider.GetRequiredService<SingletonValue>());
            Assert.Same(host.Services.GetRequiredService<IHostEnvironment>(), second.ServiceProvider.GetRequiredService<IHostEnvironment>());
            Assert.NotSame(first.ServiceProvider.GetRequiredService<ScopedValue>(), second.ServiceProvider.GetRequiredService<ScopedValue>());
            previous = singleton;
        }
        Assert.Equal(2, worker.Boots);
    }

    private static HostApplicationBuilder Builder(string environment = "Production") => Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings { EnvironmentName = environment });

    public sealed class SingletonValue;
    public sealed class ScopedValue;
    public sealed class CaptiveSingleton(ScopedValue value) { public ScopedValue Value { get; } = value; }
    public sealed class CallerOwned : IDisposable {
        public int Disposals { get; private set; }
        public int Value { get; set; }

        public void Dispose() { Disposals++; Value = 0; }
    }
}
