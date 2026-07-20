import { describe, expect, it } from 'vitest';
import { isEnglishFallback, primerBody, primerTitle } from './primerText';

const doc = {
	title: 'Combat and showdowns',
	titleNl: 'Combat en showdowns',
	body: 'In a showdown a Unit deals damage equal to its Might (§402.3).',
	bodyNl: 'In een showdown deelt een Unit schade gelijk aan zijn Might (§402.3).'
};

describe('primerText', () => {
	it('toont de Nederlandse weergave als die er is', () => {
		expect(primerBody(doc)).toBe(doc.bodyNl);
		expect(primerTitle(doc)).toBe('Combat en showdowns');
		expect(isEnglishFallback(doc)).toBe(false);
	});

	it('valt terug op de canonieke Engelse tekst, nooit op een leeg vak', () => {
		// Ontbrekende vertaling (AI-uitval of afgekeurd door de
		// speltermen-waarborg) is een verwacht pad, geen storing.
		expect(primerBody({ ...doc, bodyNl: null })).toBe(doc.body);
		expect(primerBody({ ...doc, bodyNl: '   ' })).toBe(doc.body);
		expect(primerBody({ title: doc.title, body: doc.body })).toBe(doc.body);
		expect(primerTitle({ ...doc, titleNl: null })).toBe('Combat and showdowns');
	});

	it('meldt wanneer de Engelse tekst getoond wordt', () => {
		expect(isEnglishFallback({ ...doc, bodyNl: null })).toBe(true);
		expect(isEnglishFallback({ ...doc, bodyNl: '' })).toBe(true);
	});

	it('houdt de speltermen intact — hier wordt niets vertaald', () => {
		// Deze laag kiest alleen wélke tekst getoond wordt; vertalen gebeurt
		// bij de generatie, achter de review-poort.
		for (const term of ['showdown', 'Unit', 'Might', '§402.3'])
			expect(primerBody(doc)).toContain(term);
	});
});
