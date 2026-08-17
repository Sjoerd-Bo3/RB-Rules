// Kop- en bodytekst voor de foutpagina (`+error.svelte`, #219). Pure functie,
// zodat de status → tekst-afbeelding los te unit-testen is (de Svelte-component
// blijft dan puur presentatie). Nederlands, in lijn met de rest van de UI.

export interface ErrorCopy {
	/** True bij een 404 → de "verdwaalde poro"-variant met terug-links. */
	is404: boolean;
	/** Kop; bij niet-404 is dit `status + boodschap`. */
	heading: string;
	/** Eén rustige regel eronder. */
	body: string;
}

export function errorCopy(status: number, message?: string): ErrorCopy {
	if (status === 404) {
		return {
			is404: true,
			heading: '404 — deze pagina bestaat niet (meer)',
			body: 'De poro heeft overal gezocht, maar deze pagina niet gevonden. Misschien is de link verouderd of verplaatst.'
		};
	}

	// Kop = status + boodschap; val terug op een nette generieke tekst als er
	// geen bruikbare boodschap is (server- vs. client-fout).
	const fallback =
		status >= 500 ? 'Er ging iets mis aan onze kant.' : 'Deze pagina kon niet worden geladen.';
	return {
		is404: false,
		heading: `${status} — ${message?.trim() || fallback}`,
		body: 'Probeer het straks opnieuw, of ga terug naar het overzicht.'
	};
}
