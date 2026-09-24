import "@mantine/core/styles.layer.css";
import "./theme/global.css";

import type { TokenCredential } from "@azure/identity";
import { MantineProvider } from "@mantine/core";
import type { HostContextValue, PortalModule } from "../../shared/interfaces";
import { PortalShell } from "./shell/PortalShell";
import { puckColorSchemeManager, puckCssVariablesResolver, puckTheme } from "./theme/theme";

// Served on its own (no host), the portal has no account; account pages explain that rather than call out.
const anonymousCredential: TokenCredential = {
  getToken: async () => null,
};

/**
 * The portal's federated entry. The host passes its context (account, credential, store); the studio served on
 * its own passes none.
 */
function App({ context }: { context?: HostContextValue }) {
  const tokenCredential = context?.serviceProvider.tryGet("tokenCredential") ?? anonymousCredential;

  return (
    <MantineProvider
      colorSchemeManager={puckColorSchemeManager}
      cssVariablesResolver={puckCssVariablesResolver}
      defaultColorScheme="auto"
      theme={puckTheme}
    >
      <PortalShell context={context} tokenCredential={tokenCredential} />
    </MantineProvider>
  );
}

export default App satisfies PortalModule["default"];
