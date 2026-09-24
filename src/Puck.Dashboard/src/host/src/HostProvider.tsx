import { useMsal } from "@azure/msal-react";
import { createContext, useEffect } from "react";
import type { HostContextValue, HostProviderProperties } from "../../shared/interfaces";

export const HostContext = createContext<HostContextValue>(undefined!);

export default function HostProvider({
  children,
  onboarding,
  serviceProvider,
  signInScopes,
  tokenCredential,
}: HostProviderProperties) {
  const { accounts, inProgress, instance } = useMsal();
  // The active-account cache entry can lag or be absent even when accounts exist (it is only
  // repaired in an effect, which triggers no re-render) — fall back to the first account so
  // consumers never see "signed in but no account".
  const activeAccount = instance.getActiveAccount() ?? (0 < accounts.length ? accounts[0] : null);
  const isSignedIn = "none" === inProgress && 0 < accounts.length;

  serviceProvider.tryAdd("tokenCredential", () => tokenCredential);

  useEffect(() => {
    if (isSignedIn) {
      if (!instance.getActiveAccount()) {
        instance.setActiveAccount(accounts[0]);
      }

      onboarding.send({ type: "SIGNED_IN" });
    }
  }, [accounts, instance, isSignedIn, onboarding]);

  const result: HostContextValue = {
    activeAccount: activeAccount,
    isSignedIn: isSignedIn,
    onboarding: onboarding,
    serviceProvider: serviceProvider,
    signIn: async () =>
      await instance.loginRedirect({
        // Return the user to the page they signed in from, not the site root.
        redirectStartPage: window.location.href,
        scopes: signInScopes,
      }),
    signOut: async () =>
      await instance.logoutRedirect({
        logoutHint: activeAccount?.idTokenClaims?.login_hint,
      }),
  };

  return <HostContext.Provider value={result}>{children}</HostContext.Provider>;
}
