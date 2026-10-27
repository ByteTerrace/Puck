using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Puck.World;

/// <summary>A scheduled-test worker's reusable container. Every ordinary host boot gets a fresh scope, including
/// services normally registered as singletons. Only dependency construction plans survive between documents;
/// factories and instances come from the current boot. The native host owns all startup, shutdown and disposal.</summary>
internal sealed class WorldTestHost : IDisposable {
    private ServiceProvider? m_provider;
    private RegistrationShape[]? m_shape;

    public int Boots { get; private set; }

    public IHost Build(HostApplicationBuilder builder) {
        var development = builder.Environment.IsDevelopment();

        builder.ConfigureContainer(factory: new Factory(this, new DefaultServiceProviderFactory(options: new ServiceProviderOptions {
            ValidateOnBuild = development,
            ValidateScopes = development,
        })));
        return builder.Build();
    }

    private IServiceProvider CreateProvider(IServiceCollection registrations, IServiceProviderFactory<IServiceCollection> fallback) {
        var descriptors = registrations.ToArray();
        // A changed shape cannot reuse construction plans. Keyed registrations have a different factory contract;
        // a host extension introducing either receives an ordinary independent container instead.
        if (descriptors.Any(predicate: static descriptor => descriptor.IsKeyedService) ||
            ((m_shape is { } shape) && !SameShape(descriptors: descriptors, shape: shape))) {
            Boots++;
            return fallback.CreateServiceProvider(containerBuilder: registrations);
        }

        if (m_provider is null) {
            IServiceCollection services = new ServiceCollection();

            services.AddScoped<Run>();
            for (var index = 0; (index < descriptors.Length); index++) {
                var descriptor = descriptors[index];
                var lifetime = ((descriptor.Lifetime == ServiceLifetime.Singleton) ? ServiceLifetime.Scoped : descriptor.Lifetime);

                if (descriptor.ImplementationType is { } implementation) {
                    services.Add(item: ServiceDescriptor.Describe(serviceType: descriptor.ServiceType, implementationType: implementation, lifetime: lifetime));
                } else {
                    var slot = index;

                    services.Add(item: ServiceDescriptor.Describe(descriptor.ServiceType, sp => {
                        var current = sp.GetRequiredService<Run>().Descriptors[slot];

                        return (current.ImplementationInstance ?? current.ImplementationFactory!(sp));
                    }, lifetime));
                }
            }
            m_provider = services.BuildServiceProvider(options: new ServiceProviderOptions { ValidateScopes = true });
            // Keep identities, never the first boot's closures or instances.
            m_shape = descriptors.Select(selector: RegistrationShape.Of).ToArray();
            Boots++;
        }
        var scope = m_provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<Run>().Descriptors = descriptors;
        // HostApplicationBuilder installs this as its application provider before resolving its normal IHost.
        // That host disposes this provider (the scope), while this worker retains the root construction plans.
        return scope.ServiceProvider;
    }
    private static bool SameShape(RegistrationShape[] shape, ServiceDescriptor[] descriptors) {
        if (shape.Length != descriptors.Length) { return false; }
        for (var index = 0; (index < shape.Length); index++) {
            if (shape[index] != RegistrationShape.Of(descriptor: descriptors[index])) { return false; }
        }
        return true;
    }

    public void Dispose() => m_provider?.Dispose();

    private readonly record struct RegistrationShape(Type Service, ServiceLifetime Lifetime, Type? Implementation, Type? Instance) {
        public static RegistrationShape Of(ServiceDescriptor descriptor) => new(
            descriptor.ServiceType, descriptor.Lifetime, descriptor.ImplementationType, descriptor.ImplementationInstance?.GetType());
    }
    private sealed class Run : IDisposable {
        public ServiceDescriptor[] Descriptors { get; set; } = [];

        public void Dispose() => Descriptors = [];
    }
    private sealed class Factory(WorldTestHost owner, IServiceProviderFactory<IServiceCollection> fallback) : IServiceProviderFactory<IServiceCollection> {
        public IServiceCollection CreateBuilder(IServiceCollection services) => fallback.CreateBuilder(services: services);
        public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder) => owner.CreateProvider(fallback: fallback, registrations: containerBuilder);
    }
}
