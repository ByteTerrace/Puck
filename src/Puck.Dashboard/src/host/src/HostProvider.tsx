import { AccessToken, TokenCredential } from "@azure/identity";
import {
  type PublicClientApplication,
  InteractionRequiredAuthError,
} from "@azure/msal-browser";
import { useMsal } from "@azure/msal-react";
import { createContext, useEffect, useRef } from "react";
import {
  HostContextValue,
  HostProviderProperties,
} from "../../shared/interfaces";
import { ensureOnboarded } from "../../shared/onboarding";
import { createHostStore } from "../../shared/store";

let isRedirecting = false;

class PublicClientApplicationTokenCredential implements TokenCredential {
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

export const HostContext = createContext<HostContextValue>(undefined!);

export default function HostProvider({
  children,
  serviceProvider,
  signInScopes,
}: HostProviderProperties) {
  const { accounts, inProgress, instance } = useMsal();
  const publicClientApplication = instance as PublicClientApplication;
  const tokenCredential = new PublicClientApplicationTokenCredential(publicClientApplication);
  // The active-account cache entry can lag or be absent even when accounts exist (it is only
  // repaired in an effect, which triggers no re-render) — fall back to the first account so
  // consumers never see "signed in but no account".
  const activeAccount =
    publicClientApplication.getActiveAccount() ??
    (0 < accounts.length ? accounts[0] : null);
  const createStoreResult = useRef<ReturnType<typeof createHostStore>>(null);

  if (!createStoreResult.current) {
    createStoreResult.current = createHostStore(tokenCredential);
  }

  serviceProvider.tryAdd("tokenCredential", () => tokenCredential);

  const { middleware, reducers, store } = createStoreResult.current;
  const isSignedIn = "none" === inProgress && 0 < accounts.length;

  useEffect(() => {
    if (isSignedIn) {
      if (!publicClientApplication.getActiveAccount()) {
        publicClientApplication.setActiveAccount(accounts[0]);
      }

      store.dispatch(ensureOnboarded(signInScopes));
    }
  }, [accounts, isSignedIn, publicClientApplication, signInScopes, store]);
  const result: HostContextValue & { store: any } = {
    activeAccount: activeAccount,
    isSignedIn: isSignedIn,
    serviceProvider: serviceProvider,
    signIn: async () =>
      await publicClientApplication.loginRedirect({
        // Return the user to the page they signed in from, not the site root.
        redirectStartPage: window.location.href,
        scopes: signInScopes,
      }),
    signOut: async () =>
      await publicClientApplication.logoutRedirect({
        logoutHint: activeAccount?.idTokenClaims?.login_hint,
      }),
    store: {
      addMiddleware: middleware.addMiddleware,
      setReducer: reducers.set,
      value: store,
    },
  };

  return <HostContext.Provider value={result}>{children}</HostContext.Provider>;
}
