import { Text, UnstyledButton, type UnstyledButtonProps } from "@mantine/core";
import type { ComponentPropsWithoutRef, ReactNode } from "react";
import classes from "./ExplorerItem.module.css";

type ExplorerItemProps = UnstyledButtonProps & Omit<ComponentPropsWithoutRef<"button">, keyof UnstyledButtonProps | "type"> & {
  /** The item's identifier, set in monospace and ellipsized to the explorer's width. */
  readonly name: string;
  /** A compact trailing mark on the name's line: a presence badge, a kind. */
  readonly aside?: ReactNode;
  /** One or two dimmed lines under the name. */
  readonly description?: ReactNode;
  readonly selected: boolean;
};

/**
 * One selectable entry in a workspace explorer: a button whose `aria-pressed` carries the selection, with a
 * monospace name that ellipsizes rather than wraps, an optional trailing mark, and an optional description.
 */
export function ExplorerItem({ name, aside, description, selected, ...props }: ExplorerItemProps) {
  return (
    <UnstyledButton {...props} aria-pressed={selected} className={classes.item} title={name} type="button">
      <span className={classes.head}>
        <span className={classes.name}>{name}</span>
        {aside}
      </span>
      {description && (
        <Text c="dimmed" className={classes.description} component="span" lineClamp={2} size="xs">
          {description}
        </Text>
      )}
    </UnstyledButton>
  );
}
