import { ActionIcon, Tooltip, useComputedColorScheme, useMantineColorScheme } from "@mantine/core";
import { RiMoonLine, RiSunLine } from "@remixicon/react";

/** Flips between light and dark from whichever scheme is showing, then remembers the choice. */
export function ColorSchemeToggle() {
  const { setColorScheme } = useMantineColorScheme();
  const computed = useComputedColorScheme("dark", { getInitialValueInEffect: true });
  const next = computed === "dark" ? "light" : "dark";

  return (
    <Tooltip label={`Use the ${next} theme`}>
      <ActionIcon aria-label={`Use the ${next} theme`} color="gray" onClick={() => setColorScheme(next)} size="lg">
        {computed === "dark" ? <RiSunLine size={18} /> : <RiMoonLine size={18} />}
      </ActionIcon>
    </Tooltip>
  );
}
