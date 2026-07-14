import { describe, expect, it } from 'vitest';
import { approachNotice } from './approach';

// Aanpak-terugmelding (#153): alleen bekende redenen geven een melding —
// een onbekende sleutel (nieuwere API) mag nooit een lege of kapotte
// melding renderen.
describe('approachNotice', () => {
	it('vertaalt quota-terugval naar de dagtegoed-melding', () => {
		expect(approachNotice('quota')).toContain('dagtegoed voor Grondig is op');
	});

	it('vertaalt de foto-regel en de uit-stand', () => {
		expect(approachNotice('photo')).toContain('foto-pad');
		expect(approachNotice('disabled')).toContain('niet beschikbaar');
	});

	it('geeft null bij geen of onbekende reden', () => {
		expect(approachNotice(null)).toBeNull();
		expect(approachNotice(undefined)).toBeNull();
		expect(approachNotice('')).toBeNull();
		expect(approachNotice('iets-nieuws')).toBeNull();
	});
});
