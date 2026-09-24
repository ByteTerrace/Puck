import { RiBookOpenLine, RiDatabase2Line, RiHistoryLine, RiPlanetLine } from "@remixicon/react";
import type { ReactNode } from "react";
import type { Section } from "./sections";

export interface NavigationItem {
  description: string;
  icon: ReactNode;
  label: string;
  section: Section;
}

export interface NavigationGroup {
  items: readonly NavigationItem[];
  label: string;
}

/** The portal's sections, grouped the way the navbar presents them. */
export const navigation: readonly NavigationGroup[] = [
  {
    items: [
      { description: "Author and preview worlds", icon: <RiPlanetLine size={18} />, label: "World Studio", section: "studio" },
      { description: "Engine manual and API", icon: <RiBookOpenLine size={18} />, label: "Documentation", section: "docs" },
    ],
    label: "Puck",
  },
  {
    items: [
      { description: "Your storage account", icon: <RiDatabase2Line size={18} />, label: "Cloud Storage", section: "data" },
      { description: "Changes to your account", icon: <RiHistoryLine size={18} />, label: "Audit Trail", section: "audit" },
    ],
    label: "Account",
  },
];

export function navigationItem(section: Section): NavigationItem {
  return navigation.flatMap((group) => group.items).find((item) => item.section === section)!;
}
