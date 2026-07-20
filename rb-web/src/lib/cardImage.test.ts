import { describe, it, expect } from 'vitest';
import { cardAspect, cardAlt, cardTint, isLandscape, isNewCard } from './cardImage';

describe('cardAspect', () => {
	it('gebruikt de echte maat van een liggende battlefield', () => {
		// Regressie #269: 66 van de 1178 kaarten zijn battlefields; met één
		// hardgecodeerde 744/1039 + object-fit: cover werden die bijgesneden.
		expect(cardAspect({ imageWidth: 1039, imageHeight: 744 })).toBe('1039 / 744');
	});

	it('gebruikt de echte maat van een staande kaart', () => {
		expect(cardAspect({ imageWidth: 744, imageHeight: 1039 })).toBe('744 / 1039');
	});

	it.each([
		['zonder maat', {}],
		['met alleen breedte', { imageWidth: 744 }],
		['met nul', { imageWidth: 0, imageHeight: 0 }],
		['met null', { imageWidth: null, imageHeight: null }]
	])('valt %s terug op staand', (_naam, card) => {
		expect(cardAspect(card)).toBe('744 / 1039');
	});

	it('valt ook zonder kaart terug op staand', () => {
		expect(cardAspect(null)).toBe('744 / 1039');
		expect(cardAspect(undefined)).toBe('744 / 1039');
	});
});

describe('isLandscape', () => {
	it('herkent alleen breder-dan-hoog als liggend', () => {
		expect(isLandscape({ imageWidth: 1039, imageHeight: 744 })).toBe(true);
		expect(isLandscape({ imageWidth: 744, imageHeight: 1039 })).toBe(false);
		expect(isLandscape({})).toBe(false);
		expect(isLandscape(null)).toBe(false);
	});
});

describe('cardAlt', () => {
	it('gebruikt de alt-tekst van de bron', () => {
		expect(
			cardAlt({ imageAltText: 'Riftbound Battlefield: Abandoned Hall. Doet iets.' })
		).toBe('Riftbound Battlefield: Abandoned Hall. Doet iets.');
	});

	it('valt terug op de kaartnaam als er geen alt-tekst is', () => {
		expect(cardAlt({ name: 'Abandoned Hall' })).toBe('Abandoned Hall');
		expect(cardAlt({ imageAltText: '   ' }, 'Abandoned Hall')).toBe('Abandoned Hall');
	});

	it('geeft een lege string als er niets bekend is', () => {
		expect(cardAlt(null)).toBe('');
		expect(cardAlt({})).toBe('');
	});
});

describe('cardTint', () => {
	it('gebruikt de dominante kleur van de kaart', () => {
		expect(cardTint({ imageColorPrimary: '#222c44' })).toBe('#222c44');
		expect(cardTint({ imageColorPrimary: '#ABC' })).toBe('#ABC');
	});

	it.each([
		[undefined],
		[null],
		[''],
		['rood'],
		['#12345'],
		// De waarde belandt in een style-waarde: alles wat geen hexkleur is
		// wordt geweigerd, ook als de API het ooit zou doorlaten.
		['#fff;background:url(javascript:alert(1))'],
		['red" onload="alert(1)']
	])('valt terug op het ontwerptoken bij %p', (kleur) => {
		expect(cardTint({ imageColorPrimary: kleur })).toBe('var(--surface-deep)');
	});
});

describe('isNewCard', () => {
	it('herkent Riots New-markering', () => {
		expect(isNewCard({ flags: ['New'] })).toBe(true);
		expect(isNewCard({ flags: ['new'] })).toBe(true);
	});

	it('is onwaar zonder markering', () => {
		expect(isNewCard({ flags: [] })).toBe(false);
		expect(isNewCard({})).toBe(false);
		expect(isNewCard(null)).toBe(false);
	});
});
