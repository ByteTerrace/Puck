using Azure.Identity;

namespace Puck.Azure.Functions;

public interface IUserCredentialContext
{
    OnBehalfOfCredential UserContext { get; set; }
}

public sealed class UserCredentialContext : IUserCredentialContext
{
    public OnBehalfOfCredential UserContext {
        get => (field ?? throw new InvalidOperationException(message: "An on-behalf-of credential has not been set for the current request."));
        set {
            ArgumentNullException.ThrowIfNull(argument: value);

            if (field is not null) {
                throw new InvalidOperationException(message: "An on-behalf-of credential has already been set for the current request.");
            }

            field = value;
        }
    }
}

