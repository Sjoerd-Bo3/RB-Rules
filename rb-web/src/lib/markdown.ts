import { marked } from 'marked';

// Rulings komen als markdown uit het LLM. We escapen eerst alle HTML zodat
// alleen door marked gegenereerde opmaak overblijft (geen injectie mogelijk),
// en renderen daarna GFM (tabellen, lijsten, quotes).
const escapeHtml = (s: string) =>
	s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

export function renderMarkdown(src: string): string {
	return marked.parse(escapeHtml(src), { async: false, gfm: true, breaks: true });
}
