import { describe, expect, it } from 'vitest';
import { relationBulkActionsVisible } from './reviewBulk';

// Review-fix #199 (findings 3/5/8): de bulk-knoppen per aanbevelingsgroep
// horen alléén in de te-reviewen-weergave — daar zijn telling, zichtbare
// items en de actie-scope van rb-api hetzelfde universum (unreviewed én
// niet gearchiveerd). In elke andere chip-weergave zou de knop iets anders
// tonen dan hij beslist.
describe('relationBulkActionsVisible', () => {
	it('toont de bulk-acties in de default- (te reviewen) weergave', () => {
		expect(relationBulkActionsVisible('')).toBe(true);
	});

	it('toont ze ook bij het expliciete unreviewed-filter (zelfde universum)', () => {
		expect(relationBulkActionsVisible('unreviewed')).toBe(true);
	});

	it.each(['accepted', 'rejected', 'archived', 'all'])(
		'verbergt ze in de %s-weergave',
		(filter) => {
			expect(relationBulkActionsVisible(filter)).toBe(false);
		}
	);
});
