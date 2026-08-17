/**
 * Weergave van de spelbegrip-primer (#266). De primer wordt sinds #187/#197
 * canoniek in het Engels opgeslagen (dicht bij de officiële bewoording); de
 * API levert daar een Nederlandse weergave bij, die bij de generatie is
 * gemaakt en door dezelfde review-poort ging als de Engelse tekst.
 *
 * Hier staat alleen de keuze wát er getoond wordt — nooit een vertaling: die
 * hoort bij de generatie, niet bij het renderen. Ontbreekt de Nederlandse
 * tekst (AI-uitval of een vertaling die de speltermen-waarborg niet haalde),
 * dan tonen we het Engels; een leeg vak is geen optie.
 */
export interface PrimerText {
	title: string;
	titleNl?: string | null;
	body: string;
	bodyNl?: string | null;
}

/** De te tonen tekst: Nederlands waar we het hebben, anders de canonieke Engelse. */
export function primerBody(doc: PrimerText): string {
	return nonEmpty(doc.bodyNl) ?? doc.body;
}

/** De te tonen conceptnaam; zelfde terugval als de tekst. */
export function primerTitle(doc: PrimerText): string {
	return nonEmpty(doc.titleNl) ?? doc.title;
}

/**
 * Toont dit doc de canonieke Engelse tekst? Dan zegt de pagina dat er
 * eerlijk bij — een Engelse alinea tussen Nederlandse is anders een raadsel.
 */
export function isEnglishFallback(doc: PrimerText): boolean {
	return nonEmpty(doc.bodyNl) === undefined;
}

function nonEmpty(s: string | null | undefined): string | undefined {
	return s && s.trim() ? s : undefined;
}
