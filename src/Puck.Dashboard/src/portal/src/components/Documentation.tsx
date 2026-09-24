import { Group, Paper, SimpleGrid, Stack, Text, ThemeIcon, Title, VisuallyHidden } from "@mantine/core";
import { RiArrowRightUpLine, RiBookOpenLine, RiCodeSSlashLine } from "@remixicon/react";
import type { ReactNode } from "react";
import classes from "./Documentation.module.css";
import { Well } from "../ui/Well";
import { PageHeader } from "./PageHeader";

const MANUAL_URL = "/reference/overview.html";
const REFERENCE_URL = "/reference/index.html";

interface DocumentationCardProps {
  children: ReactNode;
  href: string;
  icon: ReactNode;
  title: string;
}

function DocumentationCard({ children, href, icon, title }: DocumentationCardProps) {
  return (
    <Paper
      className={classes.card}
      component="a"
      href={href}
      p="md"
      rel="noopener noreferrer"
      target="_blank"
      withBorder
    >
      <Group align="flex-start" gap="md" wrap="nowrap">
        <ThemeIcon radius="md" size={40} variant="light">
          {icon}
        </ThemeIcon>
        <Stack gap={4}>
          <Group gap={6} wrap="nowrap">
            <Title order={2} size="h4">
              {title}
            </Title>
            <RiArrowRightUpLine className={classes.arrow} size={16} />
            <VisuallyHidden>(opens in a new tab)</VisuallyHidden>
          </Group>
          <Text c="dimmed" className="prose" size="sm">
            {children}
          </Text>
        </Stack>
      </Group>
    </Paper>
  );
}

/** The documentation front door: the engine manual and the API reference, with the manual's overview embedded. */
export default function Documentation() {
  return (
    <Stack gap="lg">
      <PageHeader kicker="Puck" title="Documentation">
        How the engine works and how to build with it, from the manual's overview down to every public API.
      </PageHeader>
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
        <DocumentationCard href={MANUAL_URL} icon={<RiBookOpenLine size={22} />} title="Engine manual">
          Concepts first: document-defined worlds, the deterministic simulation, presentation, and hosted machines.
        </DocumentationCard>
        <DocumentationCard href={REFERENCE_URL} icon={<RiCodeSSlashLine size={22} />} title="API reference">
          Every public type and member of the Puck libraries, generated from the source.
        </DocumentationCard>
      </SimpleGrid>
      <Well>
        <iframe className={classes.frame} src={MANUAL_URL} title="Puck documentation" />
      </Well>
    </Stack>
  );
}
