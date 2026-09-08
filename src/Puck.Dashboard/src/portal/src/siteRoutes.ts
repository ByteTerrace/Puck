export type Section = "audit" | "data" | "studio" | "docs";

export function sectionFromLocation({ hostname, pathname }: Pick<Location, "hostname" | "pathname">): Section {
  if (pathname === "/docs" || pathname.startsWith("/docs/") || (pathname === "/" && hostname.startsWith("docs."))) return "docs";
  if (pathname === "/audit" || pathname.startsWith("/audit/")) return "audit";
  if (pathname === "/data" || pathname.startsWith("/data/")) return "data";
  return "studio";
}

export function sectionPath(section: Section, hostname: string): string {
  if (section === "studio") return hostname.startsWith("puck.") ? "/" : "/puck";
  if (section === "docs") return hostname.startsWith("docs.") ? "/" : "/docs";
  return `/${section}`;
}
