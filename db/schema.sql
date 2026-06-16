-- Riftbound Rules Companion — Postgres schema (MVP: change-tracker).
-- pgvector wordt in Fase 2 gebruikt voor rule_chunk.embedding.
CREATE EXTENSION IF NOT EXISTS vector;

-- Bronnen-register (met relevantie/rank)
CREATE TABLE IF NOT EXISTS source (
  id            TEXT PRIMARY KEY,
  name          TEXT NOT NULL,
  url           TEXT NOT NULL,
  type          TEXT NOT NULL,                      -- 'official' | 'community'
  trust_tier    SMALLINT NOT NULL,                  -- 1 (officieel) .. 4 (overig)
  rank          INTEGER NOT NULL DEFAULT 0,         -- fijnafstemming binnen tier
  parser        TEXT NOT NULL,                      -- 'html' | 'pdf' | 'json_api'
  cadence       TEXT NOT NULL DEFAULT 'daily',
  enabled       BOOLEAN NOT NULL DEFAULT TRUE,
  last_hash     TEXT,
  last_modified TEXT,
  last_checked  TIMESTAMPTZ
);

-- Documentversies per ingest (snapshot per bron)
CREATE TABLE IF NOT EXISTS document (
  id           BIGSERIAL PRIMARY KEY,
  source_id    TEXT NOT NULL REFERENCES source(id),
  content      TEXT NOT NULL,
  content_hash TEXT NOT NULL,
  retrieved_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS document_source_idx ON document(source_id, retrieved_at DESC);

-- Changelog / diffs (voedt de change-tracker feed)
CREATE TABLE IF NOT EXISTS change (
  id          BIGSERIAL PRIMARY KEY,
  source_id   TEXT NOT NULL REFERENCES source(id),
  change_type TEXT NOT NULL DEFAULT 'unknown',      -- ban|errata|core-rule|tournament-rule|set-release|editorial
  severity    TEXT NOT NULL DEFAULT 'medium',       -- high|medium|low
  summary     TEXT,                                 -- AI-samenvatting (Fase 2)
  meaning     TEXT,                                 -- "wat betekent dit" (Fase 2)
  diff        TEXT,                                 -- toegevoegd/verwijderd/gewijzigd
  detected_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS change_detected_idx ON change(detected_at DESC);

-- Tegenstrijdigheden (conflicten-inbox) — Fase 2
CREATE TABLE IF NOT EXISTS conflict (
  id              BIGSERIAL PRIMARY KEY,
  topic           TEXT NOT NULL,
  source_a_id     TEXT REFERENCES source(id),
  source_b_id     TEXT REFERENCES source(id),
  kind            TEXT NOT NULL,                    -- 'stale' | 'contradiction'
  winner_source_id TEXT REFERENCES source(id),
  status          TEXT NOT NULL DEFAULT 'open',     -- open|reviewed|resolved
  detected_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Correctie-/override-laag (recursive self-improvement) — Fase 2
CREATE TABLE IF NOT EXISTS correction (
  id          BIGSERIAL PRIMARY KEY,
  scope       TEXT NOT NULL,                        -- 'card' | 'rule_section' | 'answer'
  ref         TEXT NOT NULL,
  text        TEXT NOT NULL,
  provenance  TEXT,
  status      TEXT NOT NULL DEFAULT 'unverified',   -- unverified|verified
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
  verified_at TIMESTAMPTZ
);

-- ─── Kaart-database (Riftcodex; update-bestendig via upsert) ───────────────
CREATE TABLE IF NOT EXISTS card_set (
  set_id       TEXT PRIMARY KEY,            -- 'OGN'
  name         TEXT NOT NULL,
  published_on DATE,
  card_count   INTEGER,
  synced_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS card (
  riftbound_id     TEXT PRIMARY KEY,         -- 'ogn-011-298'
  name             TEXT NOT NULL,
  type             TEXT,
  supertype        TEXT,
  rarity           TEXT,
  domains          TEXT[] NOT NULL DEFAULT '{}',
  energy           INTEGER,
  might            INTEGER,
  power            INTEGER,
  set_id           TEXT,
  set_label        TEXT,
  collector_number INTEGER,
  text_plain       TEXT,
  image_url        TEXT,
  tags             TEXT[] NOT NULL DEFAULT '{}',
  updated_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS card_name_idx ON card (lower(name));
CREATE INDEX IF NOT EXISTS card_set_idx ON card (set_id);
