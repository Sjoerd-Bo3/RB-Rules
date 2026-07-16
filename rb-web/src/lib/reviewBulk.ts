/**
 * Bulk-acties per aanbevelingsgroep op de relatie-reviewqueue (#199,
 * review-fix findings 3/5/8): de knoppen renderen alléén in de
 * te-reviewen-weergave — daar zijn de groepstelling, de zichtbare items en
 * de actie-scope van rb-api hetzelfde universum (status "unreviewed" én
 * niet gearchiveerd). In elke andere chip-weergave (accepted/rejected/
 * archived/all) zou de knop een ander universum tonen dan hij beslist.
 *
 * Zowel de default-weergave (geen filter) als het expliciete
 * ?filter=unreviewed tellen: AdminOverviewService.RelationsAsync levert
 * voor beide exact dezelfde query (unreviewed + niet gearchiveerd).
 */
export function relationBulkActionsVisible(filter: string): boolean {
	return filter === '' || filter === 'unreviewed';
}
