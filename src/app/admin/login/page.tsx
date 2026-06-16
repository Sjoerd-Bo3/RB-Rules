"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";

export default function AdminLogin() {
  const router = useRouter();
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError("");
    const res = await fetch("/api/admin/login", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ password }),
    });
    setBusy(false);
    if (res.ok) {
      router.push("/admin");
      router.refresh();
    } else {
      const j = await res.json().catch(() => ({}));
      setError(j.error ?? "Inloggen mislukt");
    }
  }

  return (
    <>
      <h1>Beheer — inloggen</h1>
      <p className="subtitle">Toegang tot bronnenbeheer en handmatige scans.</p>
      <form onSubmit={submit} className="card" style={{ maxWidth: 380 }}>
        <label>
          Wachtwoord
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoFocus
          />
        </label>
        {error && <p style={{ color: "var(--high)" }}>{error}</p>}
        <button type="submit" disabled={busy}>
          {busy ? "Bezig…" : "Inloggen"}
        </button>
      </form>
    </>
  );
}
