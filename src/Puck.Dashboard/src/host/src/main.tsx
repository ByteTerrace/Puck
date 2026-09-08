import {
  AuthenticationResult,
  BrowserCacheLocation,
  EventMessage,
  EventType,
  LogLevel,
  PublicClientApplication,
} from "@azure/msal-browser";
import { MsalProvider } from "@azure/msal-react";
import React from "react";
import ReactDOM from "react-dom/client";
import { HostServiceProviders } from "../../shared/interfaces";
import App from "./App";
import HostProvider from "./HostProvider";
import { initializeModuleFederation, RemoteConfig } from "./remote";
import { DefaultServiceProvider } from "./serviceProvider";

// A share link carries a recipient-bound SAS in the fragment (never sent to
// any server). Stash it before MSAL's redirect handling can clobber the hash,
// normalize the route to /data, and announce it so an already-running app
// reacts to share links clicked mid-session (those only change the hash).
const captureShare = () => {
  if (!location.hash.startsWith("#share=")) {
    return false;
  }

  try {
    sessionStorage.setItem(
      "byteterrace.pendingShare",
      decodeURIComponent(location.hash.slice("#share=".length)),
    );
  } catch {
    // Storage can be unavailable; the link simply won't carry over.
  }

  history.replaceState(null, "", `/data${location.search}`);

  return true;
};

captureShare();
window.addEventListener("hashchange", () => {
  if (captureShare()) {
    window.dispatchEvent(new CustomEvent("byteterrace-share"));
  }
});

const urlSearchParams = new URLSearchParams(location.search);
const msalConfiguration = {
  clientId:
    urlSearchParams.get("msalClientId") ??
    "e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7",
  signInScopes: urlSearchParams.has("msalSignInScopes")
    ? urlSearchParams.getAll("msalSignInScopes")
    : ["https://api.byteterrace.com/user_impersonation"],
  tenantId:
    urlSearchParams.get("msalTenantId") ??
    "e09734be-ca09-41ec-b70d-98f5536fb774",
};
const publicClientApplication = new PublicClientApplication({
  auth: {
    authority: `https://login.microsoftonline.com/${msalConfiguration.tenantId}`,
    clientCapabilities: [],
    clientId: msalConfiguration.clientId,
    redirectUri: location.origin,
  },
  cache: {
    cacheLocation: BrowserCacheLocation.SessionStorage,
    cacheRetentionDays: 3,
  },
  system: {
    allowPlatformBroker: false,
    allowRedirectInIframe: false,
    loggerOptions: {
      correlationId: undefined,
      logLevel: LogLevel.Trace,
      piiLoggingEnabled: false,
    },
    preventCorsPreflight: false,
  },
  telemetry: {
    application: {
      appName: "FdeHack Portal",
      appVersion: "0.0.1",
    },
  },
});
const remotes: RemoteConfig[] = [
  {
    baseUrl:
      urlSearchParams.get("portalBaseUrl") ??
      (import.meta.env.DEV
        ? "http://localhost:61101"
        : `${location.origin}/portal`),
    name: "portal",
    requestId: undefined,
  },
];
const serviceProvider = new DefaultServiceProvider<HostServiceProviders>();

if ("serviceWorker" in navigator) {
  navigator.serviceWorker
    .register("/sw.js")
    .then((registration) => registration.update())
    .catch((error) => console.error("Failed to register service worker:", error));
}

publicClientApplication.addEventCallback((event: EventMessage) => {
  if (EventType.LOGIN_SUCCESS === event.eventType && event.payload) {
    const payload = event.payload as AuthenticationResult;

    publicClientApplication.setActiveAccount(payload.account);
  }
});

// MSAL requires initialize() before any other API (getAllAccounts included).
Promise.all([
  initializeModuleFederation(remotes),
  publicClientApplication.initialize(),
])
  .then(() => {
    const accounts = publicClientApplication.getAllAccounts();

    if (accounts && 0 < accounts.length) {
      publicClientApplication.setActiveAccount(accounts[0]);
    }
    ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
      <React.StrictMode>
        <MsalProvider instance={publicClientApplication}>
          <HostProvider
            serviceProvider={serviceProvider}
            signInScopes={msalConfiguration.signInScopes}
          >
            <App />
          </HostProvider>
        </MsalProvider>
      </React.StrictMode>,
    );
  })
  .catch((error) => {
    console.error("Failed to initialize federation:", error);
    document.getElementById("root")!.innerHTML = `
      <div style="padding: 2rem; color: red;">
        <h1>Application Load Failed</h1>
        <p>Could not initialize Module Federation. Please check the console for details.</p>
      </div>
    `;
  });
