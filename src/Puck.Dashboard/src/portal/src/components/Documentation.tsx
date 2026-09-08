import { Anchor, Stack, Title } from "@mantine/core";

export default function Documentation() {
  return (
    <Stack gap="sm">
      <Title order={1}>Documentation</Title>
      <Anchor href="/reference/index.html" target="_blank" rel="noopener noreferrer">
        Open the API reference in a new tab
      </Anchor>
      <iframe
        title="Puck documentation"
        src="/reference/overview.html"
        style={{ width: "100%", height: "calc(100dvh - 200px)", minHeight: 400, border: 0 }}
      />
    </Stack>
  );
}
