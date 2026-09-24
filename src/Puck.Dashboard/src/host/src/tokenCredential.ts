import type { AccessToken, TokenCredential } from "@azure/identity";
import { InteractionRequiredAuthError, type PublicClientApplication } from "@azure/msal-browser";

let isRedirecting = false;

/**
 * The signed-in user's tokens, silently where MSAL can and by redirect where it cannot. A request made while a redirect
 * is under way never settles: the page is leaving.
 */
export class PublicClientApplicationTokenCredential implements TokenCredential {
  private readonly publicClientApplication: PublicClientApplication;

  constructor(publicClientApplication: PublicClientApplication) {
    this.publicClientApplication = publicClientApplication;
  }

  async getToken(scopes: string | string[]): Promise<AccessToken> {
    if (!isRedirecting) {
      const scopesAsArray = Array.isArray(scopes) ? scopes : [scopes];

      try {
        const account =
          this.publicClientApplication.getActiveAccount() ??
          this.publicClientApplication.getAllAccounts()[0];
        const response = await this.publicClientApplication.acquireTokenSilent({
          account: account,
          scopes: scopesAsArray,
        });

        return {
          expiresOnTimestamp: response.expiresOn!.getTime(),
          token: response.accessToken,
          tokenType: "Bearer",
        };
      } catch (e) {
        if (!(e instanceof InteractionRequiredAuthError)) {
          throw e;
        }

        if (!isRedirecting) {
          isRedirecting = true;

          try {
            await this.publicClientApplication.acquireTokenRedirect({
              scopes: scopesAsArray,
            });
          } catch (e) {
            isRedirecting = false;

            throw e;
          }
        }
      }
    }

    return new Promise<AccessToken>(() => {});
  }
}
