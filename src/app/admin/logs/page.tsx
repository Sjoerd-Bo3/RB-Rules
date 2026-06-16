"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";

interface LogRow {
  id: number;
  kind: string;
  ref: string | null;
  status: string;
  detail: string | null;
  created_at: string;
}

const KINDS = ["", "scan", "cards", "embed", "conflicts", "graph"];

function statusClass(s: string): string {
  if (s === "error") return "sev-high";
  if (s === "changed" || s === "new") return "sev-medium";
  return "sev-low";
}

export default function AdminLogs() {
  const router = useRouter();
  const [logs, setLogs] = useState<LogRow[]>([]);
  const [kind, setKind] = useState("");

  async function load(k = kind) {
    const res = await fetch(`/api/admin/logs${k ? `?kind=${k}` : ""}`);
    if (res.status === 401) return router.push("/admin/login");
    if (res.ok) setLogs(await res.json());
  }
  useEffect(() => {
    load("");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function clear() {
    if (!confirm("Alle logregels verwijderen?")) return;
    await fetch("/api/admin/logs", { method: "DELETE" });
    load();
  }

  return (
    <>
      <div className="admin-head">
        <h1>Logs</h1>
        <div>
          <Link href="/admin">
            <button className="ghost">← Beheer</button>
          </Link>
          <button onClick={() => load()}>Vernieuwen</button>
          <button onClick={clear} className="ghost">
            Wissen
          </button>
        </div>
      </div>

      <div className="action-row" style={{ margin: "8px 0 16px" }}>
        {KINDS.map((k) => (
          <button
            key={k || "all"}
            className={k === kind ? "" : "ghost"}
            onClick={() => {
              setKind(k);
              load(k);
            }}
          >
            {k || "alles"}
          </button>
        ))}
      </div>

      {logs.length === 0 ? (
        <p className="empty">Nog geen logregels.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Tijd</th>
              <th>Soort</th>
              <th>Ref</th>
              <th>Status</th>
              <th>Detail</th>
            </tr>
          </thead>
          <tbody>
            {logs.map((l) => (
              <tr key={l.id}>
                <td className="meta" style={{ whiteSpace: "nowrap" }}>
                  {new Date(l.created_at).toLocaleString("nl-NL")}
                </td>
                <td>{l.kind}</td>
                <td className="meta">{l.ref ?? "—"}</td>
                <td>
                  <span className={`badge ${statusClass(l.status)}`}>{l.status}</span>
                </td>
                <td>{l.detail ?? ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
