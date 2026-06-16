import { askClaude } from "@/lib/ai";

export interface Classification {
  change_type: string; // ban|errata|core-rule|tournament-rule|set-release|editorial
  severity: "high" | "medium" | "low";
  summary: string;
  meaning: string;
}

const SYSTEM = `Je bent een Riftbound TCG regels-analist. Je krijgt een diff van een
regelbron. Classificeer de wijziging en antwoord UITSLUITEND met JSON, exact dit schema:
{"change_type": "...", "severity": "...", "summary": "...", "meaning": "..."}
- change_type ∈ ban | errata | core-rule | tournament-rule | set-release | editorial
- severity ∈ high (verandert legaliteit/interactie) | medium (verduidelijking) | low (redactioneel)
- summary: korte, feitelijke samenvatting (NL)
- meaning: "wat betekent dit voor spelers" in 1-2 zinnen (NL)
Geen tekst buiten de JSON.`;

function extractJson(raw: string): unknown {
  const start = raw.indexOf("{");
  const end = raw.lastIndexOf("}");
  if (start === -1 || end === -1) throw new Error("geen JSON");
  return JSON.parse(raw.slice(start, end + 1));
}

/** Classificeer een wijziging met Claude. Geeft null bij geen AI of fout. */
export async function classifyChange(
  sourceName: string,
  diff: string,
): Promise<Classification | null> {
  try {
    const raw = await askClaude({
      task: "cheap",
      system: SYSTEM,
      prompt: `Bron: ${sourceName}\n\nDiff:\n${diff.slice(0, 4000)}`,
    });
    const o = extractJson(raw) as Partial<Classification>;
    const severity =
      o.severity === "high" || o.severity === "low" ? o.severity : "medium";
    return {
      change_type: typeof o.change_type === "string" ? o.change_type : "unknown",
      severity,
      summary: typeof o.summary === "string" ? o.summary : "",
      meaning: typeof o.meaning === "string" ? o.meaning : "",
    };
  } catch {
    return null;
  }
}
