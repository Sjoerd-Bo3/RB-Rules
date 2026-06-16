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
  const [asked, setAsked] = useState("");
  const [fix, setFix] = useState("");
  const [fixDone, setFixDone] = useState(false);

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
      setAsked(question);
      setFix("");
      setFixDone(false);
    } else {
      setError(j.error ?? "Er ging iets mis");
    }
  }

  async function submitFix(e: React.FormEvent) {
    e.preventDefault();
    if (!fix.trim()) return;
    const res = await fetch("/api/feedback", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ question: asked, text: fix }),
    });
    if (res.ok) setFixDone(true);
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

          <div className="fix-box">
            {fixDone ? (
              <p className="meta">
                Bedankt — je correctie is opgeslagen en wordt na verificatie meegenomen.
              </p>
            ) : (
              <form onSubmit={submitFix}>
                <label className="meta" htmlFor="fix">
                  Klopt dit niet? Geef de juiste ruling (gaat naar verificatie):
                </label>
                <textarea
                  id="fix"
                  rows={2}
                  value={fix}
                  onChange={(e) => setFix(e.target.value)}
                  placeholder="Bijv.: Volgens de errata van 30-3 mag dit juist wél, omdat…"
                />
                <button type="submit" className="ghost" style={{ marginTop: 8 }}>
                  Correctie insturen
                </button>
              </form>
            )}
          </div>
        </article>
      )}
    </>
  );
}
