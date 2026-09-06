using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Puck.Azure.Functions.HealthChecks;

public sealed class DataProtectionHealthCheck(IServiceProvider serviceProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) {
        try {
            var data = new Dictionary<string, object>();
            var description = default(string);
            var keyManagementOptions = serviceProvider
                .GetRequiredService<IOptions<KeyManagementOptions>>()
                .Value;
            var status = HealthStatus.Healthy;

            if (keyManagementOptions.XmlEncryptor is null) {
                data[key: "xmlEncryptor"] = "Not configured.";
                status = HealthStatus.Degraded;
            }
            else {
                data[key: "xmlEncryptor"] = keyManagementOptions.XmlEncryptor.GetType().FullName!;
            }

            if (keyManagementOptions.XmlRepository is null) {
                data[key: "xmlRepository"] = "Not configured.";
                status = HealthStatus.Degraded;
            }
            else {
                data[key: "xmlRepository"] = keyManagementOptions.XmlRepository.GetType().FullName!;
            }

            var dataProtectionProvider = serviceProvider.GetRequiredService<IDataProtectionProvider>();
            var plaintext = "ping";
            var protector = dataProtectionProvider.CreateProtector(purpose: nameof(DataProtectionHealthCheck));
            var encryptResult = protector.Protect(plaintext: plaintext);
            var decryptResult = protector.Unprotect(protectedData: encryptResult);

            if (decryptResult != plaintext) {
                description = "Uknown error occurred during round trip test.";
                status = HealthStatus.Unhealthy;
            }

            return new HealthCheckResult(
                data: data,
                description: description,
                status: status
            );
        }
        catch (Exception e) {
            return new HealthCheckResult(
                exception: e,
                status: context.Registration.FailureStatus
            );
        }
    }
}

