import type { TokenCredential } from "@azure/identity";
import { Button } from "@mantine/core";
import { RiLockLine } from "@remixicon/react";
import { lazy, Suspense } from "react";
import type { HostContextValue } from "../../../shared/interfaces";
import { EmptyState } from "../ui/EmptyState";
import { ErrorBoundary } from "../ui/ErrorBoundary";
import { SectionFallback } from "../ui/SectionFallback";
import { navigationItem } from "./navigation";
import { sectionRequiresAccount, type Section } from "./sections";

const AuditView = lazy(() => import("../components/AuditView"));
const DataExplorer = lazy(() => import("../components/DataExplorer"));
const Documentation = lazy(() => import("../components/Documentation"));
const WorldStudio = lazy(() => import("../components/world/WorldStudio"));

interface SectionOutletProps {
  context: HostContextValue | undefined;
  section: Section;
  tokenCredential: TokenCredential;
}

function SignInPrompt({ context, section }: { context: HostContextValue | undefined; section: Section }) {
  const item = navigationItem(section);

  return (
    <EmptyState
      action={
        context ? (
          <Button onClick={() => void context.signIn()}>Sign in</Button>
        ) : undefined
      }
      icon={<RiLockLine size={22} />}
      title={`Sign in to open ${item.label}`}
    >
      {context
        ? `${item.label} shows your own account's records, so it needs you signed in.`
        : `${item.label} needs the dashboard host's sign-in, which this locally served studio does not have.`}
    </EmptyState>
  );
}

/** The active section's page, loaded on demand and contained by its own error boundary. */
export function SectionOutlet({ context, section, tokenCredential }: SectionOutletProps) {
  const item = navigationItem(section);
  const userObjectId = context?.activeAccount?.localAccountId;

  if (sectionRequiresAccount(section) && !(context?.isSignedIn && userObjectId)) {
    return <SignInPrompt context={context} section={section} />;
  }

  return (
    <ErrorBoundary key={section} subject={item.label}>
      <Suspense fallback={<SectionFallback label={`Loading ${item.label}`} />}>
        {section === "docs" ? (
          <Documentation />
        ) : section === "data" ? (
          <DataExplorer tokenCredential={tokenCredential} userObjectId={userObjectId!} />
        ) : section === "audit" ? (
          <AuditView tokenCredential={tokenCredential} userObjectId={userObjectId!} />
        ) : (
          <WorldStudio />
        )}
      </Suspense>
    </ErrorBoundary>
  );
}
