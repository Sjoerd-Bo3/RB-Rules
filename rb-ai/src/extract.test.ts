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
  sections: ["section:core-4.2b", "section:core-7.1"],
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

  it("leest sections als string-array mee, junk-items eruit (#315)", () => {
    // Dit veld stuurde rb-api al sinds #286 mee, maar het werd hier nooit
    // gelezen — de GOVERNED_BY-verankering bestond daardoor alleen op papier.
    const r = parseInteractionExtractRequest({
      ...baseInteractionReq,
      sections: ["section:core-4.2b", 42, "  ", "section:core-7.1"],
    });
    assert.ok(r.ok);
    assert.deepEqual(r.request.sections, ["section:core-4.2b", "section:core-7.1"]);
  });

  it("sections afwezig → lege lijst (rb-api-versies zonder #286 blijven werken)", () => {
    const { sections: _weg, ...zonder } = baseInteractionReq;
    const r = parseInteractionExtractRequest(zonder);
    assert.ok(r.ok);
    assert.deepEqual(r.request.sections, []);
  });
});

describe("de vaste tool-vorm (#312)", () => {
  it("is request-onafhankelijk: geen vocabulaire in schema of description", () => {
    // DE kern van #312: dezelfde vorm voor elke aanroep, dus een byte-stabiel
    // request-prefix (prompt-cache) en later warm-boot-baar. De description mag
    // geen enkele aangeboden waarde meer bevatten.
    const desc = interactionToolDescription();
    for (const term of [
      "mechanic:Deflect",
      "mechanic:Assault",
      "Showdown",
      "COUNTERS",
      "section:core-4.2b",
    ]) {
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
      "governed_by (sectie-refs): section:core-4.2b | section:core-7.1",
      baseInteractionReq.text,
    ]) {
      assert.ok(p.includes(verwacht), `ontbreekt in prompt: ${verwacht}`);
    }
  });

  it("laat lege vocabulaire-assen weg uit de prompt", () => {
    const p = interactionPromptText({ ...baseInteractionReq, statusLexicon: [] });
    assert.equal(p.includes("status-lexicon"), false);
  });

  it("zonder aangeboden secties geen governed_by-regel in de prompt (#315)", () => {
    const p = interactionPromptText({ ...baseInteractionReq, sections: [] });
    assert.equal(p.includes("governed_by"), false);
  });

  it("governed_by zit in de VASTE vorm en wordt niet weggestript (#315)", () => {
    // De vorm is request-onafhankelijk (#312): het veld bestaat óók zonder
    // aangeboden secties — de sectie-poort zit in de narekening. Zonder dit
    // veld stript de zod-parse elke emit en kan het model per constructie
    // nooit een anker leveren (precies de bug van #315).
    const schema = z.object(buildInteractionToolShape());
    const parsed = schema.parse({
      interactions: [
        { from: "x", to: "y", kind: "K", interacts: true, governed_by: "section:core-4.2b" },
        { from: "x", to: "y", kind: "K", interacts: true, governed_by: null },
        { from: "x", to: "y", kind: "K", interacts: true },
      ],
    }) as { interactions: Array<{ governed_by?: string | null }> };
    assert.equal(parsed.interactions[0]?.governed_by, "section:core-4.2b");
    assert.equal(
      schema.safeParse({
        interactions: [{ from: "x", to: "y", kind: "K", interacts: true, governed_by: 7 }],
      }).success,
      false,
      "governed_by moet een string of null blijven",
    );
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

  it("een aangeboden sectie-ref overleeft als governed_by (#315, overleef-richting)", () => {
    const r = enforceInteractionVocabulary(
      [{ ...geldig, governed_by: "section:core-4.2b" }],
      baseInteractionReq,
    );
    assert.equal(r.rejected, 0);
    assert.equal(r.accepted[0]?.governed_by, "section:core-4.2b");
    // En het item eromheen blijft ongeschonden — een anker mag niets kosten.
    assert.equal(r.accepted[0]?.from, "mechanic:Deflect");
    assert.equal(r.accepted[0]?.kind, "COUNTERS");
  });

  it("een verzonnen sectie-ref wordt ge-nuld, het item blijft (#315, muur-semantiek)", () => {
    // Exact de .NET-anker-poort (InteractionExtraction.ParseDetailed): buiten
    // de aangeboden lijst → null, nooit het item weigeren dat rb-api behoudt.
    const r = enforceInteractionVocabulary(
      [{ ...geldig, governed_by: "section:verzonnen" }],
      baseInteractionReq,
    );
    assert.equal(r.rejected, 0, "het item zelf is geldig");
    assert.equal(r.accepted.length, 1);
    assert.equal(r.accepted[0]?.governed_by, undefined);
    assert.equal(r.accepted[0]?.explanation, geldig.explanation, "de rest overleeft");
  });

  it("sectie-refs zijn Ordinal-exact, net als from/to (#315)", () => {
    const r = enforceInteractionVocabulary(
      [{ ...geldig, governed_by: "SECTION:CORE-4.2B" }],
      baseInteractionReq,
    );
    assert.equal(r.rejected, 0);
    assert.equal(r.accepted[0]?.governed_by, undefined);
  });

  it("lege sectie-aanbieding nult élke governed_by — GEEN inLexicon-fallback (#315)", () => {
    // De .NET-muur toetst tegen een gewone HashSet: niets aangeboden = niets
    // geldig. De "lege lijst ⇒ geen poort"-fallback van window/status zou hier
    // juist RUIMER zijn dan de muur, en rb-api zou het anker alsnog nullen.
    const r = enforceInteractionVocabulary(
      [{ ...geldig, governed_by: "section:core-4.2b" }],
      { ...baseInteractionReq, sections: [] },
    );
    assert.equal(r.rejected, 0);
    assert.equal(r.accepted[0]?.governed_by, undefined);
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

// ── #323: model-aliassen en batch-extractie ──────────────────────────────────

import {
  batchCardRequest,
  batchInteractionPromptText,
  buildBatchInteractionToolShape,
  EXTRACT_MODELS,
  MAX_BATCH_CARDS,
  parseExtractModelAlias,
  parseInteractionBatchExtractRequest,
  type InteractionBatchExtractRequest,
} from "./extract.js";

describe("model-aliassen (#323)", () => {
  it("vertaalt de gesloten aliassen naar LETTERLIJKE model-ID's", () => {
    // Bewust uitgeschreven literals, nooit een assert tegen EXTRACT_MODELS
    // zelf: een assertie tegen de constante die ze bewaakt schuift mee
    // (#286/#293-les). Verander de map en deze test hoort rood te gaan.
    assert.deepEqual(parseExtractModelAlias("fable"), { ok: true, model: "claude-fable-5" });
    assert.deepEqual(parseExtractModelAlias("opus"), { ok: true, model: "claude-opus-4-8" });
    assert.deepEqual(parseExtractModelAlias("sonnet"), { ok: true, model: "claude-sonnet-4-6" });
    // 1M-contextvarianten (#323-aanvulling): SDK-notatie model[1m], gesloten map.
    assert.deepEqual(parseExtractModelAlias("fable-1m"), { ok: true, model: "claude-fable-5[1m]" });
    assert.deepEqual(parseExtractModelAlias("sonnet-1m"), { ok: true, model: "claude-sonnet-4-6[1m]" });
  });

  it("afwezig/leeg = geen override (oudere aanroepers blijven werken)", () => {
    assert.deepEqual(parseExtractModelAlias(undefined), { ok: true });
    assert.deepEqual(parseExtractModelAlias(null), { ok: true });
    assert.deepEqual(parseExtractModelAlias(""), { ok: true });
  });

  it("weigert een onbekende alias en een niet-string — nooit een vrije string richting de SDK", () => {
    assert.equal(parseExtractModelAlias("gpt-5").ok, false);
    // Een RUW model-ID is geen alias: de map is de enige toegang.
    assert.equal(parseExtractModelAlias("claude-fable-5").ok, false);
    assert.equal(parseExtractModelAlias(42).ok, false);
    assert.equal(parseExtractModelAlias({}).ok, false);
  });

  it("de map kent exact de drie afgesproken aliassen", () => {
    assert.deepEqual(Object.keys(EXTRACT_MODELS).sort(),
      ["fable", "fable-1m", "opus", "sonnet", "sonnet-1m"]);
  });

  it("een onbekende alias in een extract-request is een parse-fout (→ 400)", () => {
    const single = parseInteractionExtractRequest({ ...baseInteractionReq, model: "gpt-5" });
    assert.equal(single.ok, false);
    const goed = parseInteractionExtractRequest({ ...baseInteractionReq, model: "fable" });
    assert.ok(goed.ok);
    assert.equal(goed.request.model, "claude-fable-5");
  });
});

const batchReq: InteractionBatchExtractRequest = {
  kinds: ["COUNTERS", "MODIFIES", "GRANTS", "REQUIRES"],
  conditionKinds: ["WINDOW", "STATUS", "COST"],
  roles: ["agent", "patient"],
  windowLexicon: ["Showdown"],
  statusLexicon: ["Exhausted"],
  cards: [
    {
      code: "ogn-001",
      text: "Deflect prevents Assault damage.",
      refs: [
        { ref: "mechanic:Deflect", label: "Deflect" },
        { ref: "mechanic:Assault", label: "Assault" },
      ],
      sections: ["section:core-7.4"],
    },
    {
      code: "ogn-002",
      text: "Tank reduces Snipe damage.",
      refs: [
        { ref: "mechanic:Tank", label: "Tank" },
        { ref: "mechanic:Snipe", label: "Snipe" },
      ],
      sections: ["section:core-9.1"],
    },
  ],
};

describe("batch-request parse (#323)", () => {
  it("accepteert een geldige batch en bewaart de per-kaart-vocabulaires", () => {
    const r = parseInteractionBatchExtractRequest(batchReq);
    assert.ok(r.ok);
    assert.equal(r.request.cards.length, 2);
    assert.deepEqual(r.request.cards[1]!.sections, ["section:core-9.1"]);
  });

  it("weigert 0 kaarten en meer dan 250 kaarten (LETTERLIJKE grens)", () => {
    assert.equal(parseInteractionBatchExtractRequest({ ...batchReq, cards: [] }).ok, false);
    // 251 kaarten: één boven de uitgeschreven grens (K ≤ 250, expliciete
    // productkeuze van Sjoerd — begon op 5-15 in de issue).
    const teVeel = Array.from({ length: 251 }, (_, i) => ({
      ...batchReq.cards[0]!, code: `ogn-${1000 + i}`,
    }));
    assert.equal(parseInteractionBatchExtractRequest({ ...batchReq, cards: teVeel }).ok, false);
    assert.equal(MAX_BATCH_CARDS, 250);
    // 250 mag nog wél — de grens zelf is legaal.
    const precies = Array.from({ length: 250 }, (_, i) => ({
      ...batchReq.cards[0]!, code: `ogn-${1000 + i}`,
    }));
    assert.ok(parseInteractionBatchExtractRequest({ ...batchReq, cards: precies }).ok);
  });

  it("weigert dubbele kaartcodes, kaarten zonder code/text/refs en een onbekende alias", () => {
    const dubbel = { ...batchReq, cards: [batchReq.cards[0]!, { ...batchReq.cards[1]!, code: "ogn-001" }] };
    assert.equal(parseInteractionBatchExtractRequest(dubbel).ok, false);
    assert.equal(parseInteractionBatchExtractRequest(
      { ...batchReq, cards: [{ ...batchReq.cards[0]!, code: " " }] }).ok, false);
    assert.equal(parseInteractionBatchExtractRequest(
      { ...batchReq, cards: [{ ...batchReq.cards[0]!, text: "" }] }).ok, false);
    assert.equal(parseInteractionBatchExtractRequest(
      { ...batchReq, cards: [{ ...batchReq.cards[0]!, refs: [] }] }).ok, false);
    assert.equal(parseInteractionBatchExtractRequest({ ...batchReq, model: "gpt-5" }).ok, false);
    const fable = parseInteractionBatchExtractRequest({ ...batchReq, model: "fable" });
    assert.ok(fable.ok);
    assert.equal(fable.request.model, "claude-fable-5");
  });
});

describe("batch: per-kaart-vocabulaire en kruisbesmetting (#323)", () => {
  it("de prompt draagt per kaart een kop met code én het EIGEN vocabulaire", () => {
    const p = batchInteractionPromptText(batchReq);
    assert.ok(p.includes("Kaart 1 van 2 — code: ogn-001"));
    assert.ok(p.includes("Kaart 2 van 2 — code: ogn-002"));
    // Het Deflect-vocabulaire staat vóór de Deflect-tekst, het Tank-vocabulaire
    // vóór de Tank-tekst — per-kaart-lokaliteit.
    assert.ok(p.indexOf("mechanic:Deflect") < p.indexOf("Deflect prevents"));
    assert.ok(p.indexOf("mechanic:Tank") > p.indexOf("code: ogn-002"));
  });

  it("KRUISBESMETTING wordt gepakt: een ref uit het vocabulaire van kaart B wordt bij kaart A geweigerd", () => {
    // De wissel-test uit de opdracht: emit voor kaart A (ogn-001) een paar dat
    // alleen in het vocabulaire van kaart B (ogn-002) bestaat. De narekening
    // tegen het vocabulaire van kaart A MOET dat weigeren.
    const besmet = [
      { from: "mechanic:Tank", to: "mechanic:Snipe", kind: "COUNTERS", interacts: true },
      { from: "mechanic:Deflect", to: "mechanic:Assault", kind: "COUNTERS", interacts: true },
    ];
    const gateA = enforceInteractionVocabulary(besmet, batchCardRequest(batchReq, batchReq.cards[0]!));
    assert.equal(gateA.rejected, 1, "het Tank/Snipe-paar hoort bij kaart B, niet bij kaart A");
    assert.deepEqual(gateA.accepted.map((i) => i.from), ["mechanic:Deflect"]);
    // ...en andersom: hetzelfde besmette lijstje tegen het vocabulaire van
    // kaart B weigert precies het Deflect/Assault-paar. Samen bewijzen de twee
    // richtingen dat de poort echt PER KAART rekent, niet tegen de unie.
    const gateB = enforceInteractionVocabulary(besmet, batchCardRequest(batchReq, batchReq.cards[1]!));
    assert.equal(gateB.rejected, 1);
    assert.deepEqual(gateB.accepted.map((i) => i.from), ["mechanic:Tank"]);
  });

  it("governed_by volgt óók het per-kaart-vocabulaire (sectie van kaart B valt bij kaart A weg)", () => {
    const items = [{
      from: "mechanic:Deflect", to: "mechanic:Assault", kind: "COUNTERS", interacts: true,
      governed_by: "section:core-9.1", // sectie van kaart B
    }];
    const gate = enforceInteractionVocabulary(items, batchCardRequest(batchReq, batchReq.cards[0]!));
    assert.equal(gate.accepted.length, 1);
    assert.equal(gate.accepted[0]!.governed_by, undefined);
  });

  it("de batch-toolvorm eist een kaartcode naast de items", () => {
    const schema = z.object(buildBatchInteractionToolShape());
    assert.ok(schema.safeParse({ card: "ogn-001", interactions: [] }).success);
    assert.equal(schema.safeParse({ interactions: [] }).success, false);
  });
});
