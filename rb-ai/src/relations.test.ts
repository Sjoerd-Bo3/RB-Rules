import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { RELATIONS_MARKER, splitRelationProposals } from "./relations.js";

// #120: het voorstellenblok moet betrouwbaar van het antwoord splitsen —
// de gebruiker mag nooit rauwe JSON zien, en rb-api moet exact het blok
// krijgen dat de agent achterliet.

describe("splitRelationProposals", () => {
  it("laat een antwoord zonder marker byte-gelijk", () => {
    const answer = "**Oordeel:** Ja. [1]\n\n### Uitleg\nDeflect werkt zo.";
    assert.deepEqual(splitRelationProposals(answer), { answer });
  });

  it("splitst marker + JSON van het antwoord af", () => {
    const json = '{"relations": [{"from": "mechanic:Deflect", "to": "concept:combat", "kind": "verduidelijkt", "explanation": "Deflect grijpt in op targeting tijdens combat."}]}';
    const raw = `**Oordeel:** Nee. [1]\n\n${RELATIONS_MARKER}\n${json}`;
    const split = splitRelationProposals(raw);
    assert.equal(split.answer, "**Oordeel:** Nee. [1]");
    assert.equal(split.relations, json);
  });

  it("splitst ook met een ```json-fence achter de marker (rb-api's LlmJson is fence-tolerant)", () => {
    const raw = `Antwoord.\n\n${RELATIONS_MARKER}\n\`\`\`json\n{"relations": []}\n\`\`\``;
    const split = splitRelationProposals(raw);
    assert.equal(split.answer, "Antwoord.");
    assert.equal(split.relations, '```json\n{"relations": []}\n```');
  });

  it("marker zonder inhoud erachter levert geen relations-veld", () => {
    const split = splitRelationProposals(`Antwoord.\n\n${RELATIONS_MARKER}\n  `);
    assert.deepEqual(split, { answer: "Antwoord." });
  });

  it("de laatste marker wint wanneer de agent hem eerder citeert", () => {
    const raw = `De marker ${RELATIONS_MARKER} hoort aan het eind.\n\n${RELATIONS_MARKER}\n{"relations": []}`;
    const split = splitRelationProposals(raw);
    assert.equal(split.answer, `De marker ${RELATIONS_MARKER} hoort aan het eind.`);
    assert.equal(split.relations, '{"relations": []}');
  });

  it("alleen een voorstellenblok zonder antwoord ervoor geeft een leeg antwoord (vangnet-pad in rb-api)", () => {
    const split = splitRelationProposals(`${RELATIONS_MARKER}\n{"relations": []}`);
    assert.equal(split.answer, "");
    assert.equal(split.relations, '{"relations": []}');
  });
});
