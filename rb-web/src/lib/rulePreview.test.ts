import { describe, expect, it, vi } from 'vitest';
import {
	citationPreview,
	placePopover,
	ruleCodeFromHref,
	RulePreviewCache,
	type RulePreviewData
} from './rulePreview';

describe('ruleCodeFromHref', () => {
	it('haalt de code uit een chip-href', () => {
		expect(ruleCodeFromHref('/rules/348.2a')).toBe('348.2a');
	});

	it('decodeert percent-encoding (encodeURIComponent aan de schrijfkant)', () => {
		expect(ruleCodeFromHref('/rules/348%2E2a')).toBe('348.2a');
	});

	it('knipt query en fragment af', () => {
		expect(ruleCodeFromHref('/rules/348?source=x')).toBe('348');
		expect(ruleCodeFromHref('/rules/348#anker')).toBe('348');
	});

	it('weigert de hub, diepere paden en niet-rules-links', () => {
		expect(ruleCodeFromHref('/rules')).toBeNull();
		expect(ruleCodeFromHref('/rules/')).toBeNull();
		expect(ruleCodeFromHref('/rules/348/preview')).toBeNull();
		expect(ruleCodeFromHref('/cards/OGN-001')).toBeNull();
		expect(ruleCodeFromHref(null)).toBeNull();
	});

	it('weigert kapotte encoding en lege codes zonder te gooien', () => {
		expect(ruleCodeFromHref('/rules/%E0%A4%A')).toBeNull();
		expect(ruleCodeFromHref('/rules/%20')).toBeNull();
	});
});

describe('placePopover', () => {
	const viewport = { width: 1280, height: 800 };
	const pop = { width: 300, height: 120 };

	it('plaatst onder de link als daar ruimte is', () => {
		const link = { top: 100, bottom: 120, left: 40, right: 90 };
		const placed = placePopover(link, pop, viewport);
		expect(placed.below).toBe(true);
		expect(placed.top).toBe(126); // bottom + gap 6
		expect(placed.left).toBe(40);
	});

	it('klapt naar boven als onder geen ruimte meer is', () => {
		const link = { top: 740, bottom: 760, left: 40, right: 90 };
		const placed = placePopover(link, pop, viewport);
		expect(placed.below).toBe(false);
		expect(placed.top).toBe(740 - 6 - 120);
	});

	it('klemt horizontaal binnen het viewport', () => {
		const link = { top: 100, bottom: 120, left: 1200, right: 1260 };
		const placed = placePopover(link, pop, viewport);
		expect(placed.left).toBe(1280 - 300 - 8); // rechterrand - breedte - marge
		expect(placePopover({ ...link, left: 2, right: 40 }, pop, viewport).left).toBe(8);
	});

	it('kiest bij ruimtegebrek de ruimste kant en klemt de top', () => {
		const smallViewport = { width: 400, height: 200 };
		const tall = { width: 300, height: 400 };
		// Link bovenin: onder is ruimer, top wordt op de marge geklemd.
		const placed = placePopover({ top: 20, bottom: 40, left: 10, right: 60 }, tall, smallViewport);
		expect(placed.below).toBe(true);
		expect(placed.top).toBe(8);
	});
});

describe('citationPreview', () => {
	const citations = [
		{ section: '348', text: 'Regeltekst 348.', parents: [{ code: '3', text: 'Hoofdstuk' }, { code: '34', text: 'Ouder' }] },
		{ section: null, text: 'zonder sectie', parents: [] },
		{ section: '809.2', text: null, parents: null }
	];

	it('vindt de citatie op exacte sectiecode en pakt de directe ouder (laatste)', () => {
		expect(citationPreview(citations, '348')).toEqual({
			code: '348',
			text: 'Regeltekst 348.',
			parent: { code: '34', text: 'Ouder' }
		});
	});

	it('geeft null bij een code buiten de lijst', () => {
		expect(citationPreview(citations, '999')).toBeNull();
		expect(citationPreview(undefined, '348')).toBeNull();
	});

	it('kan met ontbrekende tekst en ouders overweg', () => {
		expect(citationPreview(citations, '809.2')).toEqual({ code: '809.2', text: null, parent: null });
	});
});

describe('RulePreviewCache', () => {
	const data = (code: string): RulePreviewData => ({ code, text: `tekst ${code}`, parent: null });

	it('dedupliceert gelijktijdige verzoeken tot één in-flight per code', async () => {
		let release!: (v: RulePreviewData | null) => void;
		const fetcher = vi.fn(
			() => new Promise<RulePreviewData | null>((resolve) => (release = resolve))
		);
		const cache = new RulePreviewCache(fetcher);
		const [a, b] = [cache.get('348'), cache.get('348')];
		release(data('348'));
		expect(await a).toEqual(data('348'));
		expect(await b).toEqual(data('348'));
		expect(fetcher).toHaveBeenCalledTimes(1);
	});

	it('cachet resultaten per code — een tweede hover kost geen fetch meer', async () => {
		const fetcher = vi.fn(async (code: string) => data(code));
		const cache = new RulePreviewCache(fetcher);
		expect(await cache.get('348')).toEqual(data('348'));
		expect(await cache.get('348')).toEqual(data('348'));
		expect(await cache.get('809.2')).toEqual(data('809.2'));
		expect(fetcher).toHaveBeenCalledTimes(2);
	});

	it('cachet een echte 404 (null) — die is een antwoord', async () => {
		const fetcher = vi.fn(async () => null);
		const cache = new RulePreviewCache(fetcher);
		expect(await cache.get('999')).toBeNull();
		expect(await cache.get('999')).toBeNull();
		expect(fetcher).toHaveBeenCalledTimes(1);
	});

	it('cachet een transportfout NIET en meldt hem als undefined', async () => {
		const fetcher = vi
			.fn<(code: string) => Promise<RulePreviewData | null>>()
			.mockRejectedValueOnce(new Error('502'))
			.mockResolvedValueOnce(data('348'));
		const cache = new RulePreviewCache(fetcher);
		expect(await cache.get('348')).toBeUndefined();
		expect(await cache.get('348')).toEqual(data('348'));
		expect(fetcher).toHaveBeenCalledTimes(2);
	});
});
