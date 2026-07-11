import { marked } from 'marked';
import { iconifyTokens } from '$lib/rbtokens';

// Rulings komen als markdown uit het LLM en gaan via {@html} de pagina in.
// Twee verdedigingslinies:
// 1. '&' en '<' escapen vóór het parsen — raw HTML in de brontekst kan dan
//    nooit als tag renderen. '>' blijft staan zodat blockquotes werken.
// 2. Link-/afbeeldings-URL's whitelisten (http/https/mailto/relatief) — marked
//    saneert zelf geen javascript:-URL's.
const escapeHtml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');

const safeUrl = (href: string): string | null => {
	const trimmed = href.trim();
	if (/^(https?:|mailto:)/i.test(trimmed)) return trimmed;
	if (trimmed.startsWith('/') || trimmed.startsWith('#')) return trimmed;
	return null;
};

// Attribuut-escape: een '"' in de URL mag nooit uit het href-attribuut breken.
const escapeAttr = (s: string) => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;');

marked.use({
	renderer: {
		link({ href, text }) {
			const url = safeUrl(href);
			return url
				? `<a href="${escapeAttr(url)}" target="_blank" rel="noopener noreferrer">${text}</a>`
				: text;
		},
		image({ text }) {
			return text; // geen externe afbeeldingen in antwoorden
		}
	}
});

export function renderMarkdown(src: string): string {
	const html = marked.parse(escapeHtml(src), { async: false, gfm: true, breaks: true });
	// Antwoorden citeren kaartteksten — icon-tokens ook hier als echte iconen.
	return iconifyTokens(html);
}
