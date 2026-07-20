import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { z } from "zod";
import {
  buildInteractionToolShape,
  buildPredicateToolShape,
  enforceInteractionVocabulary,
  enforcePredicateVocabulary,
  interactionPromptText,
  interactionToolDescription,
  parseInteractionExtractRequest,
  parsePredicateExtractRequest,
  predicatePromptText,
  predicateToolDescription,
  type InteractionExtractRequest,
  type PredicateExtractRequest,
} from "./extract.js";

// #226 (§3.1) + #312: de tool-vorm is een CONSTANTE en het vocabulaire reist als
// prompt-invoer mee; de gesloten-vraag-regel ("nooit een term buiten het
// aangeboden lijstje", CLAUDE.md) wordt DETERMINISTISCH nagerekend. Deze tests
// bewaken die narekening op gedrag, in beide richtingen: een term buiten het
// vocabulaire wordt geweigerd, én geldige inhoud overleeft ONGESCHONDEN — een
// weiger-test zonder overleef-assert bewijst niets (#295-les).

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

describe("de vaste tool-vorm (#312)", () => {
  it("is request-onafhankelijk: geen vocabulaire in schema of description", () => {
    // DE kern van #312: dezelfde vorm voor elke aanroep, dus een byte-stabiel
    // request-prefix (prompt-cache) en later warm-boot-baar. De description mag
    // geen enkele aangeboden waarde meer bevatten.
    const desc = interactionToolDescription();
    for (const term of ["mechanic:Deflect", "mechanic:Assault", "Showdown", "COUNTERS"]) {
      assert.equal(desc.includes(term), false, `vocabulaire lekt in de description: ${term}`);
    }
    // Twee aanroepen bouwen structureel dezelfde shape (geen per-request-enums).
    const a = z.object(buildInteractionToolShape());
    const geldig = {
      interactions: [
        { from: "wat:dan:ook", to: "iets:anders", kind: "VERZONNEN", interacts: true },
      ],
    };
    // De VORM valideert nog steeds (velden/types), maar de enum-poort is er
    // bewust uit: die zit nu in de narekening hieronder.
    assert.ok(a.safeParse(geldig).success);
    assert.equal(
      a.safeParse({ interactions: [{ from: "x", to: "y", kind: "Z", interacts: "ja" }] }).success,
      false,
      "interacts moet een echte boolean blijven",
    );
  });

  it("zet het volledige vocabulaire én de tekst in de prompt-invoer", () => {
    const p = interactionPromptText(baseInteractionReq);
    for (const verwacht of [
      "mechanic:Deflect (Deflect)",
      "mechanic:Assault (Assault)",
      "COUNTERS",
      "WINDOW",
      "agent",
      "Showdown",
      "Exhausted",
      baseInteractionReq.text,
    ]) {
      assert.ok(p.includes(verwacht), `ontbreekt in prompt: ${verwacht}`);
    }
  });

  it("laat lege vocabulaire-assen weg uit de prompt", () => {
    const p = interactionPromptText({ ...baseInteractionReq, statusLexicon: [] });
    assert.equal(p.includes("status-lexicon"), false);
  });
});

describe("enforceInteractionVocabulary — de deterministische narekening (#312)", () => {
  const geldig = {
    from: "mechanic:Deflect",
    to: "mechanic:Assault",
    kind: "COUNTERS",
    interacts: true,
    explanation: "Deflect prevents the damage Assault would deal.",
    conditions: [{ on_kind: "WINDOW", window: "Showdown", subject_role: "patient" }],
  };

  it("laat een geldig item ONGESCHONDEN door (overleef-richting, #295-les)", () => {
    const r = enforceInteractionVocabulary([geldig], baseInteractionReq);
    assert.equal(r.rejected, 0);
    assert.equal(r.rejectedConditions, 0);
    assert.deepEqual(r.accepted, [
      {
        from: "mechanic:Deflect",
        to: "mechanic:Assault",
        kind: "COUNTERS",
        interacts: true,
        explanation: "Deflect prevents the damage Assault would deal.",
        conditions: [
          {
            on_kind: "WINDOW",
            subject_role: "patient",
            window: "Showdown",
            status: null,
            value: null,
            operator: null,
          },
        ],
      },
    ]);
  });

  it("weigert een ref buiten de aangeboden set (regressie-eis #312)", () => {
    // Bij de enum-poort kón het model dit niet emitten; nu het schema open is
    // MOET de narekening het tegenhouden — dit is de gesloten-vraag-regel.
    const r = enforceInteractionVocabulary(
      [
        { ...geldig, to: "mechanic:Nonexistent" },
        { ...geldig, from: "card:Verzonnen" },
        geldig,
      ],
      baseInteractionReq,
    );
    assert.equal(r.rejected, 2);
    assert.equal(r.accepted.length, 1);
    assert.equal(r.accepted[0]?.to, "mechanic:Assault");
  });

  it("weigert een kind buiten het vocabulaire", () => {
    const r = enforceInteractionVocabulary([{ ...geldig, kind: "CLARIFIES" }], baseInteractionReq);
    assert.equal(r.rejected, 1);
    assert.deepEqual(r.accepted, []);
  });

  it("accepteert kind case-insensitief — de .NET-muur canonicaliseert zelf", () => {
    // Strenger dan de muur zijn zou dekking kosten: rb-api accepteert "counters".
    const r = enforceInteractionVocabulary([{ ...geldig, kind: "counters" }], baseInteractionReq);
    assert.equal(r.rejected, 0);
    assert.equal(r.accepted[0]?.kind, "counters");
  });

  it("refs zijn exact (Ordinal), net als de .NET-muur", () => {
    const r = enforceInteractionVocabulary(
      [{ ...geldig, from: "MECHANIC:DEFLECT" }],
      baseInteractionReq,
    );
    assert.equal(r.rejected, 1);
  });

  it("weigert een on_kind buiten de aangeboden assen: conditie weg, item blijft", () => {
    // Review-gat (#312): deze poortregel was onbewaakt — haal de
    // conditionKinds-check uit checkCondition en alles bleef groen.
    const r = enforceInteractionVocabulary(
      [{ ...geldig, conditions: [{ on_kind: "TIME", window: "Showdown" }] }],
      baseInteractionReq,
    );
    assert.equal(r.rejected, 0, "het item zelf is geldig");
    assert.equal(r.rejectedConditions, 1);
    assert.equal(r.accepted[0]?.conditions, undefined);
  });

  it("weigert een window buiten het lexicon: conditie weg, item blijft (muur-semantiek)", () => {
    const r = enforceInteractionVocabulary(
      [{ ...geldig, conditions: [{ on_kind: "WINDOW", window: "Midnight" }] }],
      baseInteractionReq,
    );
    assert.equal(r.rejected, 0, "het item zelf is geldig");
    assert.equal(r.rejectedConditions, 1);
    assert.equal(r.accepted[0]?.conditions, undefined);
  });

  it("een STATUS-conditie met junk-window blijft geldig — per as toetsen, zoals de muur", () => {
    const r = enforceInteractionVocabulary(
      [{ ...geldig, conditions: [{ on_kind: "STATUS", status: "Exhausted", window: "Junk" }] }],
      baseInteractionReq,
    );
    assert.equal(r.rejectedConditions, 0);
    assert.equal(r.accepted[0]?.conditions?.[0]?.status, "Exhausted");
    assert.equal(r.accepted[0]?.conditions?.[0]?.window, null, "junk-window gaat niet mee");
  });

  it("een subject_role buiten de rollen wordt ge-nuld, niet geweigerd (muur-semantiek)", () => {
    const r = enforceInteractionVocabulary(
      [{ ...geldig, conditions: [{ on_kind: "WINDOW", window: "Showdown", subject_role: "referee" }] }],
      baseInteractionReq,
    );
    assert.equal(r.rejectedConditions, 0);
    assert.equal(r.accepted[0]?.conditions?.[0]?.subject_role, null);
  });

  it("valt terug op vrije waarden wanneer een lexicon leeg is (zelfde fallback als de oude enum-bouw)", () => {
    const r = enforceInteractionVocabulary(
      [{ ...geldig, conditions: [{ on_kind: "WINDOW", window: "AnythingGoes" }] }],
      { ...baseInteractionReq, windowLexicon: [] },
    );
    assert.equal(r.rejectedConditions, 0);
    assert.equal(r.accepted[0]?.conditions?.[0]?.window, "AnythingGoes");
  });

  it("weigert vorm-junk: non-object, ontbrekende velden, interacts als string", () => {
    const r = enforceInteractionVocabulary(
      [
        "geen object",
        { from: "mechanic:Deflect" },
        { ...geldig, interacts: "ja" },
        null,
      ],
      baseInteractionReq,
    );
    assert.equal(r.rejected, 4);
    assert.deepEqual(r.accepted, []);
  });

  it("stript onbekende velden — er lekt niets richting rb-api dat de vorm niet kent", () => {
    const r = enforceInteractionVocabulary(
      [{ ...geldig, conditions: undefined, verzonnen_veld: "x" }],
      baseInteractionReq,
    );
    assert.equal(r.rejected, 0);
    assert.deepEqual(Object.keys(r.accepted[0] ?? {}).sort(), [
      "explanation",
      "from",
      "interacts",
      "kind",
      "to",
    ]);
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

describe("predicaten: vaste vorm + narekening (#312)", () => {
  it("description is request-onafhankelijk; subject en hints staan in de prompt", () => {
    const desc = predicateToolDescription();
    assert.equal(desc.includes("Accelerate"), false);
    const p = predicatePromptText(basePredicateReq);
    assert.ok(p.includes("Accelerate (mechanic:Accelerate)"));
    assert.ok(p.includes("triggers_on | prevents | grants | requires_target"));
    assert.ok(p.includes("exhaust, ready, unit"));
    assert.ok(p.includes(basePredicateReq.text));
  });

  it("de vorm valideert types, niet het vocabulaire", () => {
    const schema = z.object(buildPredicateToolShape());
    assert.ok(schema.safeParse({ predicates: [{ predicate: "wat-dan-ook", object: "x" }] }).success);
    assert.equal(schema.safeParse({ predicates: [{ predicate: "prevents" }] }).success, false);
  });

  it("weigert een predicaat buiten de aangeboden lijst, en laat een geldig paar ongeschonden door", () => {
    const r = enforcePredicateVocabulary(
      [
        { predicate: "enables", object: "exhaust" },
        { predicate: "prevents", object: "exhaust" },
      ],
      basePredicateReq,
    );
    assert.equal(r.rejected, 1);
    assert.deepEqual(r.accepted, [{ predicate: "prevents", object: "exhaust" }]);
  });

  it("weigert vorm-junk en een leeg object-token", () => {
    const r = enforcePredicateVocabulary(
      ["junk", { predicate: "prevents" }, { predicate: "prevents", object: "  " }],
      basePredicateReq,
    );
    assert.equal(r.rejected, 3);
    assert.deepEqual(r.accepted, []);
  });
});
