// Steekproef-audit (#255): het gesloten oordeel-schema en de modelkeuze.
//
// Twee dingen worden hier op GEDRAG vastgelegd (geen bron-greps — de les van
// vier afgekeurde PR's):
//  1. het oordeel-schema is dicht: precies één verdict, echte booleans — een
//     oordeel dat buiten het schema valt wordt GEWEIGERD, niet coulant gelezen;
//  2. de audit draait op het STERKERE model (task "hard" → MODEL.hard), terwijl
//     de bulk-extractie op het cheap-model blijft. De asserts gebruiken de
//     UITGESCHREVEN model-id's: een assert tegen MODEL[...] zou meeschuiven met
//     precies de regressie die hij moet vangen.
import assert from "node:assert/strict";
import { describe, it, test } from "node:test";
import { z } from "zod";
import { extractWithTool, type QueryRunner } from "./ai.js";
import {
  AUDIT_TOOL_ADDENDUM,
  auditToolDescription,
  buildAuditExtraction,
  buildAuditToolShape,
  parseInteractionAuditRequest,
} from "./audit.js";

describe("parseInteractionAuditRequest", () => {
  it("accepteert een request met tekst (system optioneel)", () => {
    const r = parseInteractionAuditRequest({ text: "claim + bewijs", system: "sys" });
    assert.ok(r.ok);
    assert.equal(r.request.text, "claim + bewijs");
    assert.equal(r.request.system, "sys");
  });

  it("weigert een request zonder text", () => {
    assert.equal(parseInteractionAuditRequest({ text: "   " }).ok, false);
    assert.equal(parseInteractionAuditRequest({}).ok, false);
    assert.equal(parseInteractionAuditRequest(null).ok, false);
  });
});

describe("buildAuditToolShape — het gesloten oordeel", () => {
  const schema = z.object(buildAuditToolShape());
  const geldig = {
    correct: true,
    supported_by_evidence: false,
    motivation: "The rule text does not mention this pairing.",
  };

  it("accepteert precies één verdict met echte booleans", () => {
    assert.ok(schema.safeParse({ verdicts: [geldig] }).success);
  });

  it("weigert een oordeel buiten het schema (string i.p.v. boolean)", () => {
    // Mutatie-eis (b) uit #255: een coulante lezing ("yes" ≈ true) zou het
    // gesloten oordeel openbreken — dit MOET geweigerd worden.
    const r = schema.safeParse({
      verdicts: [{ correct: "yes", supported_by_evidence: true, motivation: "x" }],
    });
    assert.equal(r.success, false);
  });

  it("weigert een ontbrekend verplicht veld", () => {
    const r = schema.safeParse({ verdicts: [{ correct: true, motivation: "x" }] });
    assert.equal(r.success, false);
  });

  it("weigert nul verdicts — een audit zonder oordeel is geen audit", () => {
    assert.equal(schema.safeParse({ verdicts: [] }).success, false);
  });

  it("weigert twee verdicts — één interactie, één oordeel", () => {
    assert.equal(schema.safeParse({ verdicts: [geldig, geldig] }).success, false);
  });
});

describe("addendum en description dwingen de vorm af", () => {
  it("noemt de toolnaam en de precies-één-eis", () => {
    assert.match(AUDIT_TOOL_ADDENDUM, /emit_audit_verdict/);
    assert.match(AUDIT_TOOL_ADDENDUM, /PRECIES ÉÉN keer/);
    assert.match(auditToolDescription(), /correct/);
    assert.match(auditToolDescription(), /supported_by_evidence/);
  });
});

// ── De endpoint-bedrading zelf (#255-review) ─────────────────────────────────
// De adversariële review toonde het gat: `task: "hard"` weghalen uit de handler
// hield tsc én alle tests groen, terwijl elke audit-rij "claude-opus-4-8" bleef
// stempelen over een run die stil op het cheap-model draaide — valse provenance
// voor exact de meting waarvan "sterker model" de pointe is. De bedrading ligt
// daarom in de pure `buildAuditExtraction`, en hier op GEDRAG vast.

describe("buildAuditExtraction — de endpoint-bedrading", () => {
  it("bedraadt task 'hard': de modelkeuze is het feature", () => {
    const opts = buildAuditExtraction({ text: "claim + bewijs", system: "sys" });
    assert.equal(opts.task, "hard");
    assert.equal(opts.toolName, "emit_audit_verdict");
    assert.equal(opts.resultKey, "verdicts");
    assert.equal(opts.addendum, AUDIT_TOOL_ADDENDUM);
    assert.equal(opts.system, "sys");
    assert.equal(opts.text, "claim + bewijs");
  });

  it("geeft het abort-signaal door (weggelopen client breekt de run af)", () => {
    const controller = new AbortController();
    assert.equal(
      buildAuditExtraction({ text: "x" }, controller.signal).signal,
      controller.signal,
    );
  });

  it("keten-test: builder → extractWithTool eindigt op het sterkere model", async () => {
    // Eén test over de HELE bedrading: valt de task uit de builder óf verandert
    // de MODEL-mapping, dan is dit rood — niet alleen de losse schakels.
    let model: string | undefined;
    const runQuery = (async function* (arg: { options: { model?: string } }) {
      model = arg.options.model;
      yield { type: "result", subtype: "success", is_error: false, result: "klaar" };
    }) as unknown as QueryRunner;

    await extractWithTool({ ...buildAuditExtraction({ text: "claim + bewijs" }), runQuery });

    assert.equal(model, "claude-opus-4-8");
  });
});

// ── Modelkeuze: de audit draait op het sterkere model ────────────────────────

/** Vang de opties waarmee extractWithTool de SDK aanroept; de nep-run slaagt
 * direct zodat er geen timeout of semaphore-wachttijd in de test zit. */
async function capturedModel(task?: "cheap" | "hard"): Promise<string | undefined> {
  let model: string | undefined;
  const runQuery = (async function* (arg: { options: { model?: string } }) {
    model = arg.options.model;
    yield { type: "result", subtype: "success", is_error: false, result: "klaar" };
  }) as unknown as QueryRunner;

  await extractWithTool({
    toolName: "emit_audit_verdict",
    description: "test",
    schema: {} as never,
    resultKey: "verdicts",
    addendum: "test",
    text: "claim + bewijs",
    ...(task ? { task } : {}),
    runQuery,
  });
  return model;
}

test("task 'hard' draait op het sterkere model (#255)", async () => {
  // Uitgeschreven literal: dít is het model dat de cheap-output beoordeelt.
  assert.equal(await capturedModel("hard"), "claude-opus-4-8");
});

test("zonder task blijft de extractie op het cheap-model — bestaand gedrag", async () => {
  assert.equal(await capturedModel(), "claude-sonnet-4-6");
  assert.equal(await capturedModel("cheap"), "claude-sonnet-4-6");
});
