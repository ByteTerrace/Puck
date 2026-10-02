using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

namespace Puck.Shaders.Tests;

/// <summary>Every public package entry point initializes complete interfaces in a fresh assembly context.</summary>
public sealed class SdfWorldPackageInitializationLawTests {
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
    [Theory]
    public void First_access_keeps_native_and_temporal_pass_interfaces_complete(string first) {
        var context = new AssemblyLoadContext(isCollectible: true, name: $"sdf-package-{first}");

        try {
            var assembly = context.LoadFromAssemblyPath(assemblyPath: typeof(SdfWorldPackage).Assembly.Location);
            var package = assembly.GetType(name: typeof(SdfWorldPackage).FullName!, throwOnError: true)!;

            object Read(string name) => package.GetProperty(bindingAttr: BindingFlags.Public | BindingFlags.Static, name: name)!.GetValue(obj: null)!;

            Assert.NotNull(@object: Read(name: first));
            Assert.Equal(["lightFrame", "lights"], Names(members: Read(name: nameof(SdfWorldPackage.LightTables))));
            Assert.Equal(["skyFrame", "skyStops", "skySoftboxes"], Names(members: Read(name: nameof(SdfWorldPackage.SkyTables))));
            var sky = Names(members: Read(name: nameof(SdfWorldPackage.SkyMembers)));
            var shadow = Names(members: Read(name: nameof(SdfWorldPackage.ShadowMembers)));
            var views = Names(members: Read(name: nameof(SdfWorldPackage.ViewsMembers)));
            var temporalViews = Names(members: Read(name: nameof(SdfWorldPackage.TemporalViewsMembers)));
            var temporalResolve = Names(members: Read(name: nameof(SdfWorldPackage.TemporalResolveMembers)));

            Assert.Equal(actualArray: temporalViews, expectedSpan: [.. views, "reactivity"]);
            Assert.Contains(collection: temporalResolve, expected: "historyColor");
            Assert.Contains(collection: temporalResolve, expected: "historySurface");

            foreach (var name in new[] { nameof(SdfWorldPackage.NativeFragment), nameof(SdfWorldPackage.Fragment), nameof(SdfWorldPackage.TemporalFragment) }) {
                var fragment = Read(name: name);
                var passes = Items(value: Property(instance: fragment, name: "Passes"));

                foreach (var pass in passes) {
                    var part = ((string)Property(instance: pass, name: "Name"));
                    var expected = part switch {
                        "sky" => sky,
                        "shadow" => shadow,
                        "views" => ((name == nameof(SdfWorldPackage.TemporalFragment)) ? temporalViews : views),
                        "resolve" when (name == nameof(SdfWorldPackage.TemporalFragment)) => temporalResolve,
                        _ => null,
                    };

                    if (expected is not null) { Assert.Equal(expected, Names(members: Property(instance: pass, name: "Members"))); }
                }
            }
        } finally {
            context.Unload();
        }
    }

    // Reflection stays inside the isolated assembly so a prior test cannot initialize the types this case observes.
    private static object Property(object instance, string name) {
        var value = instance.GetType().GetProperty(name: name)!.GetValue(obj: instance);

        Assert.NotNull(@object: value);
        return value;
    }
    private static IEnumerable<object> Items(object value) => ((IEnumerable)value).Cast<object>();
    private static string[] Names(object members) => Items(value: members).Select(selector: member => ((string)Property(instance: member, name: "Name"))).ToArray();
}
