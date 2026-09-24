import { Box, Group, Text, Title } from "@mantine/core";
import type { ReactNode } from "react";
import { Kicker } from "../ui/Kicker";

interface PageHeaderProps {
  /** The page's own actions, set at the header's trailing edge. */
  actions?: ReactNode;
  /** One sentence saying what the page is. */
  children: ReactNode;
  kicker: string;
  title: string;
}

/** An account or documentation page's opening: overline, title, one sentence, and the page's own actions. */
export function PageHeader({ actions, children, kicker, title }: PageHeaderProps) {
  return (
    <Group align="flex-end" justify="space-between" wrap="wrap">
      <Box maw={720}>
        <Kicker>{kicker}</Kicker>
        <Title mt={4} order={1} size="h2">
          {title}
        </Title>
        <Text c="dimmed" mt={6} size="sm">
          {children}
        </Text>
      </Box>
      {actions ? (
        <Group gap="xs" wrap="nowrap">
          {actions}
        </Group>
      ) : null}
    </Group>
  );
}
