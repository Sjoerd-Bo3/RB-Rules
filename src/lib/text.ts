import { createHash } from "node:crypto";

/** SHA-256 hex hash van een string — basis voor change-detection. */
export function sha256(input: string): string {
  return createHash("sha256").update(input, "utf8").digest("hex");
}

/**
 * Heel eenvoudige HTML → tekst voor de MVP: strip scripts/styles en tags,
 * normaliseer whitespace. (Fase 2 vervangt dit door nette parsing + PDF.)
 */
export function htmlToText(html: string): string {
  return html
    .replace(/<script[\s\S]*?<\/script>/gi, " ")
    .replace(/<style[\s\S]*?<\/style>/gi, " ")
    .replace(/<[^>]+>/g, " ")
    .replace(/&nbsp;/gi, " ")
    .replace(/&amp;/gi, "&")
    .replace(/\s+/g, " ")
    .trim();
}

/** Naïeve regelgebaseerde diff: regels die zijn toegevoegd/verwijderd. */
export function lineDiff(oldText: string, newText: string): string {
  const split = (t: string) =>
    new Set(t.split(/(?<=\.)\s+/).map((s) => s.trim()).filter(Boolean));
  const a = split(oldText);
  const b = split(newText);
  const added = [...b].filter((x) => !a.has(x));
  const removed = [...a].filter((x) => !b.has(x));
  const fmt = (label: string, items: string[]) =>
    items.length ? `${label}:\n` + items.map((i) => `  ${i}`).join("\n") : "";
  return [fmt("+ toegevoegd", added.slice(0, 40)), fmt("- verwijderd", removed.slice(0, 40))]
    .filter(Boolean)
    .join("\n\n");
}
