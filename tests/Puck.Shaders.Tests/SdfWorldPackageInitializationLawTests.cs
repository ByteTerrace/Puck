using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

namespace Puck.Shaders.Tests;

/// <summary>Every public package entry point initializes complete interfaces in a fresh assembly context.</summary>
public sealed class SdfWorldPackageInitializationLawTests {
    [Theory]
    [InlineData(nameof(SdfWorldPackage.MeshAtlases))]
    [InlineData(nameof(SdfWorldPackage.MeshDepthAttachment))]
    [InlineData(nameof(SdfWorldPackage.Values))]
    [InlineData(nameof(SdfWorldPackage.Tables))]
    [InlineData(nameof(SdfWorldPackage.Members))]
    [InlineData(nameof(SdfWorldPackage.ResolveMembers))]
    [InlineData(nameof(SdfWorldPackage.LightTables))]
    [InlineData(nameof(SdfWorldPackage.SkyTables))]
    [InlineData(nameof(SdfWorldPackage.SkyMembers))]
    [InlineData(nameof(SdfWorldPackage.ShadowMembers))]
    [InlineData(nameof(SdfWorldPackage.ViewsMembers))]
    [InlineData(nameof(SdfWorldPackage.TemporalViewsMembers))]
    [InlineData(nameof(SdfWorldPackage.TemporalResolveMembers))]
    [InlineData(nameof(SdfWorldPackage.NativeFragment))]
    [InlineData(nameof(SdfWorldPackage.Fragment))]
    [InlineData(nameof(SdfWorldPackage.TemporalFragment))]
    public void First_access_keeps_native_and_temporal_pass_interfaces_complete(string first) {
        var context = new AssemblyLoadContext(name: $"sdf-package-{first}", isCollectible: true);
        try {
            var assembly = context.LoadFromAssemblyPath(assemblyPath: typeof(SdfWorldPackage).Assembly.Location);
            var package = assembly.GetType(name: typeof(SdfWorldPackage).FullName!, throwOnError: true)!;
            object Read(string name) => package.GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

            Assert.NotNull(Read(first));
            Assert.Equal(["lightFrame", "lights"], Names(Read(nameof(SdfWorldPackage.LightTables))));
            Assert.Equal(["skyFrame", "skyStops", "skySoftboxes"], Names(Read(nameof(SdfWorldPackage.SkyTables))));
            var sky = Names(Read(nameof(SdfWorldPackage.SkyMembers)));
            var shadow = Names(Read(nameof(SdfWorldPackage.ShadowMembers)));
            var views = Names(Read(nameof(SdfWorldPackage.ViewsMembers)));
            var temporalViews = Names(Read(nameof(SdfWorldPackage.TemporalViewsMembers)));
            var temporalResolve = Names(Read(nameof(SdfWorldPackage.TemporalResolveMembers)));
            Assert.Equal([.. views, "reactivity"], temporalViews);
            Assert.Contains("historyColor", temporalResolve);
            Assert.Contains("historySurface", temporalResolve);

            foreach (var name in new[] { nameof(SdfWorldPackage.NativeFragment), nameof(SdfWorldPackage.Fragment), nameof(SdfWorldPackage.TemporalFragment) }) {
                var fragment = Read(name);
                var passes = Items(Property(fragment, "Passes"));
                foreach (var pass in passes) {
                    var part = (string)Property(pass, "Name");
                    var expected = part switch {
                        "sky" => sky,
                        "shadow" => shadow,
                        "views" => name == nameof(SdfWorldPackage.TemporalFragment) ? temporalViews : views,
                        "resolve" when name == nameof(SdfWorldPackage.TemporalFragment) => temporalResolve,
                        _ => null,
                    };
                    if (expected is not null) { Assert.Equal(expected, Names(Property(pass, "Members"))); }
                }
            }
        } finally {
            context.Unload();
        }
    }

    // Reflection stays inside the isolated assembly so a prior test cannot initialize the types this case observes.
    private static object Property(object instance, string name) {
        var value = instance.GetType().GetProperty(name)!.GetValue(instance);
        Assert.NotNull(value);
        return value;
    }
    private static IEnumerable<object> Items(object value) => ((IEnumerable)value).Cast<object>();
    private static string[] Names(object members) => Items(members).Select(member => (string)Property(member, "Name")).ToArray();
}
