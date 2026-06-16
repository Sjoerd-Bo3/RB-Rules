"use client";

import { useState } from "react";

interface Citation {
  n: number;
  name: string;
  url: string;
  section: string | null;
  trust: number;
}

export default function AskPage() {
  const [question, setQuestion] = useState("");
  const [answer, setAnswer] = useState("");
  const [sources, setSources] = useState<Citation[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!question.trim()) return;
    setBusy(true);
    setError("");
    setAnswer("");
    setSources([]);
    const res = await fetch("/api/ask", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ question }),
    });
    setBusy(false);
    const j = await res.json().catch(() => ({}));
    if (res.ok) {
      setAnswer(j.answer ?? "");
      setSources(j.sources ?? []);
    } else {
      setError(j.error ?? "Er ging iets mis");
    }
  }

  return (
    <>
      <h1>Vraag een ruling</h1>
      <p className="subtitle">
        Stel een regelvraag; het antwoord komt mét citaten naar de bron.
      </p>

      <form onSubmit={submit} className="card">
        <textarea
          rows={3}
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          placeholder="Bijv.: Mag ik Draven Vanquisher nog spelen in constructed?"
        />
        <button type="submit" disabled={busy} style={{ marginTop: 10 }}>
          {busy ? "Bezig…" : "Vraag"}
        </button>
      </form>

      {error && <p style={{ color: "var(--high)" }}>{error}</p>}

      {answer && (
        <article className="card">
          <p style={{ whiteSpace: "pre-wrap" }}>{answer}</p>
          {sources.length > 0 && (
            <>
              <h2 style={{ fontSize: "1rem", marginBottom: 8 }}>Bronnen</h2>
              <ol className="cites">
                {sources.map((s) => (
                  <li key={s.n}>
                    <a href={s.url} target="_blank" rel="noreferrer">
                      {s.name}
                    </a>{" "}
                    <span className="meta">
                      (trust {s.trust}
                      {s.section ? `, §${s.section}` : ""})
                    </span>
                  </li>
                ))}
              </ol>
            </>
          )}
        </article>
      )}
    </>
  );
}
