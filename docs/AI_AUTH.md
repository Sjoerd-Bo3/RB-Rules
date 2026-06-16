# AI-auth: Claude-abonnement vs. API-key

De AI-features (Q&A, classificatie, foto-redenering) draaien via de
**Claude Agent SDK** (`@anthropic-ai/claude-agent-sdk`, functie `query()`).
Die kan op twee manieren authenticeren.

## Optie A — je Claude-abonnement (OAuth) — wat je wilt
Geen per-token API-kosten; gebruik trekt op je **plan-usage** (Pro/Max).

1. Genereer eenmalig een langlevende OAuth-token met de Claude CLI:
   ```bash
   claude setup-token
   ```
2. Zet 'm in `.env`:
   ```
   CLAUDE_CODE_OAUTH_TOKEN=sk-ant-oat01-...
   ```
3. De SDK pikt deze env-var **automatisch** op (headless, geen browser nodig).

> ⚠️ **Belangrijk:** als `ANTHROPIC_API_KEY` óók gezet is, **wint die stilletjes**
> en betaal je alsnog per token. Laat `ANTHROPIC_API_KEY` dus **leeg** als je het
> abonnement gebruikt.

### Eerlijke kanttekening (ToS)
Anthropic's voorwaarden beperken OAuth-abonnementsauth tot persoonlijk/
single-tenant gebruik ("Claude Code en Claude.ai"). Eén abonnement dat de
verzoeken van **veel** publieke gebruikers bedient, valt buiten die bedoeling en
loopt tegen plan-rate-limits aan. Prima voor **persoonlijk gebruik of een kleine
besloten groep**; voor een breed publieke multi-user dienst is optie B (API-key,
ToS-conform, eigen rate-limits/budget) de nette route. De app ondersteunt beide —
jij kiest per deploy.

## Optie B — pay-per-token API-key
```
ANTHROPIC_API_KEY=sk-ant-...
```
Conform ToS voor multi-user, eigen rate-limits en budgetcontrole. Kosten: zie
[`COSTS_AND_HOSTING.md`](COSTS_AND_HOSTING.md) (~$0,005–0,02 per Q&A met Sonnet).

## Embeddings vallen er los van
Het abonnement dekt **geen** embeddings. De vector-zoekopdracht gebruikt
**Voyage** (eigen `VOYAGE_API_KEY`; gratis tier dekt de ingest).

## Hoe de app kiest
`src/lib/ai.ts` gebruikt de Agent SDK; aanwezigheid van `CLAUDE_CODE_OAUTH_TOKEN`
(abonnement) of anders `ANTHROPIC_API_KEY` bepaalt de auth. Model standaard
`claude-sonnet-4-6` voor goedkope taken, `claude-opus-4-8` voor lastige rulings.
