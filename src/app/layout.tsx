import type { Metadata, Viewport } from "next";
import Link from "next/link";
import "./globals.css";

export const metadata: Metadata = {
  title: "Riftbound Rules Companion",
  description: "Onofficiële, altijd-actuele Riftbound-regelbron met change-tracking.",
  manifest: "/manifest.json",
};

export const viewport: Viewport = {
  themeColor: "#0e1726",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="nl">
      <body>
        <header className="site-header">
          <Link href="/" className="brand">
            Riftbound <span>Rules Companion</span>
          </Link>
          <nav>
            <Link href="/">Wijzigingen</Link>
            <Link href="/sources">Bronnen</Link>
          </nav>
        </header>
        <main className="container">{children}</main>
        <footer className="site-footer">
          Onofficieel fan-project · regels blijven eigendom van Riot Games.
        </footer>
      </body>
    </html>
  );
}
