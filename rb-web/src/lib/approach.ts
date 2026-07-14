// Aanpak-keuze per vraag (#153): labels en meldingen op één plek, gedeeld
// door het formulier, het streamingpad en het antwoordpaneel.

export type Approach = 'auto' | 'fast' | 'thorough';

export const APPROACH_OPTIONS: { value: Approach; label: string; hint: string }[] = [
	{ value: 'auto', label: 'Auto', hint: 'de vraagbaak kiest zelf de aanpak' },
	{ value: 'fast', label: 'Snel', hint: 'altijd het snelle antwoordpad' },
	{
		value: 'thorough',
		label: 'Grondig',
		hint: 'de brein-agent redeneert door — duurt ±2 min en telt zwaarder mee'
	}
];

/** Nette melding wanneer de server een Grondig-keuze niet honoreerde — de
 *  reden reist als machine-sleutel mee in de respons (AgenticGate.Reason*).
 *  Onbekende of lege redenen geven null: geen melding. */
export function approachNotice(reason: string | null | undefined): string | null {
	switch (reason) {
		case 'quota':
			return 'Je dagtegoed voor Grondig is op — deze vraag is automatisch beantwoord.';
		case 'photo':
			return 'Foto-vragen volgen altijd het foto-pad — de aanpak-keuze geldt daar niet.';
		case 'disabled':
			return 'Grondig is op dit moment niet beschikbaar — deze vraag is automatisch beantwoord.';
		default:
			return null;
	}
}
