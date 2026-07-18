// Samengestelde shell (#214): vaste zijbalk links + content + contextuele
// rechterrail. Pagina's leveren opt-in rail-inhoud (een Svelte 5 snippet) via
// deze context-store — per request geïsoleerd (setContext), dus geen
// server-lekkage tussen bezoekers. Bewust de kleinste vorm: één slot met een
// soort-vlag bepaalt het mobiele gedrag.
import { getContext, setContext } from 'svelte';
import type { Snippet } from 'svelte';

/** 'filters' → op mobiel achter een "Filter"-knop in een bottom-sheet (chips
 *  wrappen, nooit horizontaal scrollen). 'context' → op mobiel gewoon onder de
 *  content. Op desktop staan beide in de rechterrail. */
export type RailKind = 'filters' | 'context';

export interface RailContent {
	snippet: Snippet;
	kind: RailKind;
	/** Aantal actieve filters — badge op de mobiele "Filter"-knop. */
	count?: number;
	/** Kop boven de rail/sheet (bv. "Filters" of "Op deze pagina"). */
	title?: string;
}

export type Theme = 'light' | 'dark';
const THEME_KEY = 'rb-theme';

export class ShellStore {
	rail = $state<RailContent | null>(null);
	drawerOpen = $state(false);
	sheetOpen = $state(false);
	/** Actief thema; null zolang niet expliciet gekozen (volgt dan het OS). */
	theme = $state<Theme | null>(null);
	/** OS-voorkeur (prefers-color-scheme: dark). Reactief, zodat het label van
	 *  de thema-schakelaar meeschakelt als de bezoeker zonder eigen keuze zijn
	 *  OS-thema wisselt (#214-review). */
	systemDark = $state(false);

	/** Effectief donker? Voor het label van de thema-schakelaar. */
	get isDark(): boolean {
		if (this.theme) return this.theme === 'dark';
		return this.systemDark;
	}

	initTheme() {
		if (typeof document === 'undefined') return;
		const attr = document.documentElement.dataset.theme;
		this.theme = attr === 'light' || attr === 'dark' ? attr : null;
		if (typeof window !== 'undefined' && window.matchMedia) {
			const mq = window.matchMedia('(prefers-color-scheme: dark)');
			this.systemDark = mq.matches;
			// De shell leeft de hele sessie; één listener, geen teardown nodig.
			mq.addEventListener('change', (e) => (this.systemDark = e.matches));
		}
	}

	setTheme(theme: Theme | null) {
		this.theme = theme;
		if (typeof document === 'undefined') return;
		if (theme) {
			document.documentElement.dataset.theme = theme;
			try {
				localStorage.setItem(THEME_KEY, theme);
			} catch {
				/* private mode */
			}
		} else {
			delete document.documentElement.dataset.theme;
			try {
				localStorage.removeItem(THEME_KEY);
			} catch {
				/* private mode */
			}
		}
	}

	toggleTheme() {
		this.setTheme(this.isDark ? 'light' : 'dark');
	}
}

const KEY = Symbol('rb-shell');

export function provideShell(): ShellStore {
	const store = new ShellStore();
	setContext(KEY, store);
	return store;
}

export function useShell(): ShellStore {
	return getContext(KEY) as ShellStore;
}
