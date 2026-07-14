// Voorverwarmsignaal voor de AI-vraagbaak (#154): de /ask-paginalaad meldt
// zich bij rb-api (dat fire-and-forget rb-ai's warme-sessie-pool triggert),
// zodat de Agent SDK-subprocess-boot van het kritieke pad af is tegen de
// tijd dat de vraag komt.

/** Vuur het signaal af zonder er ooit op te wachten: de page-load mag hier
 * niet door vertragen of falen. Zowel een rejection als een synchrone gooi
 * wordt ingeslikt — prewarm is pure best-effort. */
export function firePrewarm(send: () => Promise<unknown>): void {
	try {
		void send().catch(() => {});
	} catch {
		// synchrone fout (bv. kapotte fetch-implementatie) — ook stil
	}
}
