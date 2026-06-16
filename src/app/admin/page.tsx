"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";

interface Source {
  id: string;
  name: string;
  url: string;
  type: "official" | "community";
  trust_tier: number;
  rank: number;
  parser: string;
  cadence: string;
  enabled: boolean;
}

const EMPTY = {
  id: "",
  name: "",
  url: "",
  type: "community",
  trust_tier: 3,
  rank: 0,
  parser: "html",
  cadence: "daily",
  enabled: true,
};

export default function AdminDashboard() {
  const router = useRouter();
  const [sources, setSources] = useState<Source[]>([]);
  const [form, setForm] = useState<Record<string, unknown>>({ ...EMPTY });
  const [status, setStatus] = useState("");

  async function load() {
    const res = await fetch("/api/admin/sources");
    if (res.status === 401) return router.push("/admin/login");
    setSources(await res.json());
  }
  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function add(e: React.FormEvent) {
    e.preventDefault();
    setStatus("");
    const res = await fetch("/api/admin/sources", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(form),
    });
    if (res.ok) {
      setForm({ ...EMPTY });
      load();
    } else {
      const j = await res.json().catch(() => ({}));
      setStatus(j.error ?? "Toevoegen mislukt");
    }
  }

  async function patch(id: string, body: Partial<Source>) {
    await fetch(`/api/admin/sources/${id}`, {
      method: "PATCH",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    load();
  }

  async function del(id: string) {
    if (!confirm(`Bron "${id}" verwijderen (incl. wijzigingen)?`)) return;
    await fetch(`/api/admin/sources/${id}`, { method: "DELETE" });
    load();
  }

  async function scan(sourceId?: string) {
    setStatus(sourceId ? `Scan ${sourceId}…` : "Scan alle bronnen…");
    const res = await fetch("/api/admin/scan", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(sourceId ? { sourceId } : {}),
    });
    const j = await res.json().catch(() => ({}));
    const r = (j.results ?? []) as { sourceId: string; status: string }[];
    setStatus("Klaar: " + r.map((x) => `${x.sourceId}=${x.status}`).join(", "));
    load();
  }

  async function action(label: string, path: string) {
    setStatus(`${label}…`);
    const res = await fetch(path, { method: "POST" });
    const j = await res.json().catch(() => ({}));
    if (!res.ok) {
      setStatus(`${label}: fout — ${j.error ?? res.status}`);
      return;
    }
    setStatus(`${label}: ${JSON.stringify(j.results ?? j)}`);
  }

  async function logout() {
    await fetch("/api/admin/login", { method: "DELETE" });
    router.push("/admin/login");
  }

  return (
    <>
      <div className="admin-head">
        <h1>Beheer — bronnen</h1>
        <div>
          <button onClick={() => scan()}>Scan alles</button>
          <button onClick={logout} className="ghost">
            Uitloggen
          </button>
        </div>
      </div>
      {status && <p className="meta">{status}</p>}

      <div className="card actions">
        <strong>Acties</strong>
        <div className="action-row">
          <button onClick={() => scan()}>Scan bronnen</button>
          <button onClick={() => action("Kaarten synchroniseren", "/api/admin/cards")}>
            Kaarten synchroniseren
          </button>
          <button onClick={() => action("Index opbouwen", "/api/admin/embed")}>
            Index opbouwen (embeddings)
          </button>
          <button onClick={() => action("Conflicten checken", "/api/admin/conflicts")}>
            Conflicten checken
          </button>
          <button onClick={() => action("Graph sync", "/api/admin/graph")}>
            Graph sync (Neo4j)
          </button>
        </div>
      </div>

      <table>
        <thead>
          <tr>
            <th>Bron</th>
            <th>Type</th>
            <th>Trust</th>
            <th>Rang</th>
            <th>Cadans</th>
            <th>Aan</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {sources.map((s) => (
            <tr key={s.id}>
              <td>
                <strong>{s.name}</strong>
                <br />
                <a className="meta" href={s.url} target="_blank" rel="noreferrer">
                  {s.id}
                </a>
              </td>
              <td>{s.type}</td>
              <td>
                <input
                  type="number"
                  min={1}
                  max={4}
                  defaultValue={s.trust_tier}
                  onBlur={(e) => patch(s.id, { trust_tier: Number(e.target.value) })}
                  className="num"
                />
              </td>
              <td>
                <input
                  type="number"
                  defaultValue={s.rank}
                  onBlur={(e) => patch(s.id, { rank: Number(e.target.value) })}
                  className="num"
                />
              </td>
              <td>
                <select
                  defaultValue={s.cadence}
                  onChange={(e) => patch(s.id, { cadence: e.target.value })}
                >
                  <option value="daily">daily</option>
                  <option value="weekly">weekly</option>
                </select>
              </td>
              <td>
                <input
                  type="checkbox"
                  checked={s.enabled}
                  onChange={(e) => patch(s.id, { enabled: e.target.checked })}
                />
              </td>
              <td className="row-actions">
                <button onClick={() => scan(s.id)}>Scan</button>
                <button onClick={() => del(s.id)} className="danger">
                  ×
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <h2 style={{ marginTop: 28 }}>Bron toevoegen</h2>
      <form onSubmit={add} className="card add-form">
        <div className="grid">
          <label>
            id<input value={String(form.id)} onChange={(e) => setForm({ ...form, id: e.target.value })} />
          </label>
          <label>
            naam<input value={String(form.name)} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </label>
          <label className="wide">
            url<input value={String(form.url)} onChange={(e) => setForm({ ...form, url: e.target.value })} />
          </label>
          <label>
            type
            <select value={String(form.type)} onChange={(e) => setForm({ ...form, type: e.target.value })}>
              <option value="official">official</option>
              <option value="community">community</option>
            </select>
          </label>
          <label>
            parser
            <select value={String(form.parser)} onChange={(e) => setForm({ ...form, parser: e.target.value })}>
              <option value="html">html</option>
              <option value="pdf">pdf</option>
              <option value="json_api">json_api</option>
            </select>
          </label>
          <label>
            trust
            <input type="number" min={1} max={4} value={Number(form.trust_tier)} onChange={(e) => setForm({ ...form, trust_tier: Number(e.target.value) })} />
          </label>
          <label>
            rang
            <input type="number" value={Number(form.rank)} onChange={(e) => setForm({ ...form, rank: Number(e.target.value) })} />
          </label>
        </div>
        <button type="submit">Toevoegen</button>
      </form>
    </>
  );
}
