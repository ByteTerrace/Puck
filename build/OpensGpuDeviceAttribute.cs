namespace Puck;

/// <summary>
/// Marks a way onto the host's GPU: on a class, constructing it; on a method, constructor or property, calling it.
/// <c>Puck.Analyzers</c> holds every test class that reaches a marked member, directly or through a helper, to carry
/// <c>[Trait("Category", "Gpu")]</c> (GPU001), so a test run beside a GPU leg leaves out exactly the classes that
/// contend for the device with <c>--filter-not-trait Category=Gpu</c>. A helper that reaches a marked member carries the
/// mark itself and hands the obligation to its callers. A software device (WARP) runs on the CPU and is not marked.
/// <para>
/// The type is <c>internal</c> and linked into every project as source (see <c>Directory.Build.props</c>), as
/// <see cref="VerifiedCodeAttribute"/> is: the analyzer matches it by name, so a production assembly's copy marks its
/// members for every test assembly that references them.
/// </para>
/// </summary>
[AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
internal sealed class OpensGpuDeviceAttribute : Attribute;
