# Scraping- & ingestie-aanpak (deep-dive #3)

> Status: ontwerp. Doel: officiële bronnen betrouwbaar uitlezen, change-detection
> robuust maken, en herkomst (provenance) altijd bewaren.

## Principes
1. **Officiële Rules Hub is de ruggengraat** — bewaak de centrale index + de
   gelinkte PDF's. Community alleen als aanvulling/cross-check.
2. **Goedkoop beginnen:** eerst hash/datum-check; pas her-indexeren bij wijziging.
3. **Wees een nette bot:** respecteer robots.txt, redelijke scan-frequentie
   (1×/dag is ruim), user-agent met contact, geen hammering.
4. **Citeren, niet klakkeloos kopiëren:** sla herkomst + link op; toon fragmenten.

## Per brontype

### HTML (Rules Hub, announcements, changelogs)
- Ophalen → schoon parsen (bv. readability/DOM → markdown).
- Rules Hub levert per document een **"last updated"-datum** → gebruik die als
  primair change-signaal, met content-hash als back-up.
- Announcements (bans/updates) → datum + titel + body, gelinkt aan onderwerp.

### PDF (Core Rules, Tournament Rules, errata-documenten)
- Twee paden, afhankelijk van PDF-kwaliteit:
  - **Tekst-PDF (normaal):** parser zoals `pdfplumber`/`pypdf` → tekst per pagina.
  - **Rommelige/complexe layout:** Claude **document/vision** (PDF als input) om
    schone, gestructureerde tekst te extraheren.
- **Sectie-splitsing op regelnummering** (bv. `601.2.d`) zodat diffs en citaten
  naar exacte secties kunnen wijzen.

### JSON / API (card database)
- Bronnen: Piltover Archive / Riftcodex API / `cards.json`
  (vikkumar2021/RiftboundCardDatabase).
- Gestructureerd inlezen (id, naam, type, domains, energy, might, abilities,
  keywords, set, ban-status, errata) → voedt direct de graph-knopen.

## Change-detection pipeline (per bron, per run)
```
fetch → (hash + last-updated vergelijken)
  ├─ ongewijzigd → stop (gratis)
  └─ gewijzigd → extract → sectie-diff → AI classify (type/ernst/uitleg)
                 → conflict-check → store (met provenance) → notify
```
- Bewaar per bron: `last_hash`, `last_checked`, `last_modified`.
- Bewaar per chunk: bron-id, URL, sectie/pagina, datum, hash.

## Standaard bronnenlijst (met trust-tier)
| Bron | Type | Trust | Cadence |
|---|---|---|---|
| Rules Hub (leagueoflegends.com) | HTML index | 1 (officieel) | dagelijks |
| Core Rules PDF | PDF | 1 | dagelijks (hash) |
| Tournament Rules PDF | PDF | 1 | dagelijks (hash) |
| Errata-documenten (Origins/Spiritforged/Unleashed) | PDF/HTML | 1 | dagelijks |
| Patch Notes | HTML/PDF | 1 | dagelijks |
| Announcements / TR-changelogs | HTML | 1 | dagelijks |
| Piltover Archive | card-DB / HTML | 3 (community) | wekelijks |
| Riftcodex API / cards.json | JSON | 3 | wekelijks |
| riftbound.gg (mirrors/changelogs) | HTML | 3 | wekelijks |
| Mobalytics / RiftRank / Rift Mana / Fextralife | HTML | 3–4 | wekelijks |

> Bronnen + tiers staan straks als **config in de repo**, zodat toevoegen/herwegen
> een data-wijziging is, geen code-wijziging (sluit aan op het bronnen-register).

## Robuustheid
- **Bron onbereikbaar / parser faalt** → status in Bronnen-health, geen stille
  fout; vorige versie blijft staan.
- **Retries met backoff** bij netwerkfouten.
- **Idempotent**: zelfde input → zelfde hash → geen dubbele changes.
- **Officiële PDF's** kunnen van URL/structuur wijzigen → val terug op de Rules Hub
  als bron-van-links i.p.v. hard-coded PDF-URL's.
