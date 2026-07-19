import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { z } from "zod";
import {
  buildInteractionToolShape,
  buildPredicateToolShape,
  interactionToolDescription,
  parseInteractionExtractRequest,
  parsePredicateExtractRequest,
  predicateToolDescription,
  type InteractionExtractRequest,
  type PredicateExtractRequest,
} from "./extract.js";

// #226 (§3.1): de tool-forced extractie moet het ontologie-vocabulaire één-op-één
// vertalen naar dichtgetimmerde zod-enum-poorten. Deze tests bewaken de PURE
// vertaling (vocabulaire → schema + request-validatie) — het model KAN geen ref/
// kind/window buiten de aangeboden enums noemen, en een kale request valt af.

const baseInteractionReq: InteractionExtractRequest = {
  text: "Deflect prevents Assault damage, but only during a showdown.",
  refs: [
    { ref: "mechanic:Deflect", label: "Deflect" },
    { ref: "mechanic:Assault", label: "Assault" },
  ],
  kinds: ["COUNTERS", "MODIFIES", "GRANTS", "REQUIRES"],
  conditionKinds: ["WINDOW", "STATUS", "COST"],
  roles: ["agent", "patient"],
  windowLexicon: ["Showdown"],
  statusLexicon: ["Exhausted"],
};

describe("parseInteractionExtractRequest", () => {
  it("accepteert een volledige request", () => {
    const r = parseInteractionExtractRequest(baseInteractionReq);
    assert.ok(r.ok);
    assert.equal(r.request.refs.length, 2);
    assert.deepEqual(r.request.kinds, ["COUNTERS", "MODIFIES", "GRANTS", "REQUIRES"]);
  });

  it("weigert een request zonder text", () => {
    const r = parseInteractionExtractRequest({ ...baseInteractionReq, text: "  " });
    assert.equal(r.ok, false);
  });

  it("weigert een request zonder refs", () => {
    const r = parseInteractionExtractRequest({ ...baseInteractionReq, refs: [] });
    assert.equal(r.ok, false);
  });

  it("weigert een request zonder kinds-enum", () => {
    const r = parseInteractionExtractRequest({ ...baseInteractionReq, kinds: [] });
    assert.equal(r.ok, false);
  });

  it("negeert malformed ref-items maar houdt geldige", () => {
    const r = parseInteractionExtractRequest({
      ...baseInteractionReq,
      refs: [{ ref: "card:a", label: "A" }, { nope: true }, { ref: "  " }],
    });
    assert.ok(r.ok);
    assert.equal(r.request.refs.length, 1);
    assert.equal(r.request.refs[0]?.ref, "card:a");
  });
});

describe("buildInteractionToolShape", () => {
  const shape = buildInteractionToolShape(baseInteractionReq);
  const schema = z.object(shape);

  it("accepteert een geldige interactie binnen het vocabulaire", () => {
    const parsed = schema.safeParse({
      interactions: [
        {
          from: "mechanic:Deflect",
          to: "mechanic:Assault",
          kind: "COUNTERS",
          interacts: true,
          conditions: [{ on_kind: "WINDOW", window: "Showdown", subject_role: "patient" }],
        },
      ],
    });
    assert.ok(parsed.success);
  });

  it("weigert een ref buiten de aangeboden set (enum-poort)", () => {
    const parsed = schema.safeParse({
      interactions: [
        { from: "mechanic:Deflect", to: "mechanic:Nonexistent", kind: "COUNTERS", interacts: true },
      ],
    });
    assert.equal(parsed.success, false);
  });

  it("weigert een kind buiten het vocabulaire", () => {
    const parsed = schema.safeParse({
      interactions: [
        { from: "mechanic:Deflect", to: "mechanic:Assault", kind: "CLARIFIES", interacts: true },
      ],
    });
    assert.equal(parsed.success, false);
  });

  it("weigert een window buiten het lexicon", () => {
    const parsed = schema.safeParse({
      interactions: [
        {
          from: "mechanic:Deflect",
          to: "mechanic:Assault",
          kind: "COUNTERS",
          interacts: true,
          conditions: [{ on_kind: "WINDOW", window: "Midnight" }],
        },
      ],
    });
    assert.equal(parsed.success, false);
  });

  it("valt terug op een vrije string wanneer een lexicon leeg is", () => {
    const shapeNoLex = buildInteractionToolShape({
      ...baseInteractionReq,
      windowLexicon: [],
    });
    const schemaNoLex = z.object(shapeNoLex);
    const parsed = schemaNoLex.safeParse({
      interactions: [
        {
          from: "mechanic:Deflect",
          to: "mechanic:Assault",
          kind: "COUNTERS",
          interacts: true,
          conditions: [{ on_kind: "WINDOW", window: "AnythingGoes" }],
        },
      ],
    });
    assert.ok(parsed.success);
  });

  it("somt de aangeboden refs op in de tool-description", () => {
    const desc = interactionToolDescription(baseInteractionReq);
    assert.match(desc, /mechanic:Deflect \(Deflect\)/);
    assert.match(desc, /mechanic:Assault \(Assault\)/);
  });
});

const basePredicateReq: PredicateExtractRequest = {
  text: "Accelerate: your units do not exhaust when moving to a showdown.",
  subjectRef: "mechanic:Accelerate",
  subjectLabel: "Accelerate",
  predicates: ["triggers_on", "prevents", "grants", "requires_target"],
  objectHints: ["exhaust", "ready", "unit"],
};

describe("parsePredicateExtractRequest", () => {
  it("accepteert een volledige request", () => {
    const r = parsePredicateExtractRequest(basePredicateReq);
    assert.ok(r.ok);
    assert.equal(r.request.subjectRef, "mechanic:Accelerate");
  });

  it("weigert zonder text", () => {
    assert.equal(parsePredicateExtractRequest({ ...basePredicateReq, text: "" }).ok, false);
  });

  it("weigert zonder subjectRef", () => {
    assert.equal(
      parsePredicateExtractRequest({ ...basePredicateReq, subjectRef: " " }).ok,
      false,
    );
  });

  it("weigert zonder predicates-enum", () => {
    assert.equal(parsePredicateExtractRequest({ ...basePredicateReq, predicates: [] }).ok, false);
  });
});

describe("buildPredicateToolShape", () => {
  const schema = z.object(buildPredicateToolShape(basePredicateReq));

  it("accepteert een geldig predicaat", () => {
    const parsed = schema.safeParse({
      predicates: [{ predicate: "prevents", object: "exhaust" }],
    });
    assert.ok(parsed.success);
  });

  it("weigert een onbekend predicaat (harde enum)", () => {
    const parsed = schema.safeParse({
      predicates: [{ predicate: "enables", object: "exhaust" }],
    });
    assert.equal(parsed.success, false);
  });

  it("vereist een object-token", () => {
    const parsed = schema.safeParse({ predicates: [{ predicate: "prevents" }] });
    assert.equal(parsed.success, false);
  });

  it("zet de object-hints in de tool-description", () => {
    assert.match(predicateToolDescription(basePredicateReq), /exhaust, ready, unit/);
  });
});
