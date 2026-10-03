using System.Reflection;
using System.Runtime.Versioning;

using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>The software (WARP) device API answers every member of <see cref="IDirectXDeviceApi"/> on a software
/// device: the interface holds only what a device API does, so no implementation keeps a member alive by refusing it.
/// The law walks the interface by reflection, so a member added later is called too, and a parameter it cannot supply
/// fails the law by name.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXWarpDeviceApiLawTests {
    [Fact]
    public void TheSoftwareDeviceApiAnswersEveryMemberOfItsInterface() {
        var api = new DirectXWarpDeviceApi();
        DirectXDevice device;

        try {
            device = api.CreateWarpDevice(minimumFeatureLevel: DirectXFeatureLevel.Level110);
        } catch (DirectXException exception) {
            Assert.Skip(reason: $"no software (WARP) Direct3D 12 device on this host: {exception.Message}");

            return;
        }

        using (device) {
            var adapterLuid = api.GetAdapterLuid(deviceHandle: device.Handle);
            var refused = new List<string>();

            foreach (var member in typeof(IDirectXDeviceApi).GetMethods()) {
                var arguments = member.GetParameters().Select(selector: parameter => parameter.ParameterType switch {
                    var type when (type == typeof(DirectXFeatureLevel)) => ((object)DirectXFeatureLevel.Level110),
                    var type when ((type == typeof(long)) && (parameter.Name == "adapterLuid")) => adapterLuid,
                    var type when ((type == typeof(nint)) && (parameter.Name == "deviceHandle")) => device.Handle,
                    _ => throw new InvalidOperationException(message: $"The law supplies no argument for {member.Name}'s parameter {parameter.Name}; teach it one."),
                }).ToArray();

                try {
                    if (member.Invoke(obj: api, parameters: arguments) is IDisposable created) {
                        created.Dispose();
                    }
                } catch (TargetInvocationException exception) when ((exception.InnerException is not null)) {
                    refused.Add(item: $"{member.Name}: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
                }
            }

            Assert.Empty(collection: refused);
        }
    }
}
