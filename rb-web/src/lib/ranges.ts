/**
 * Compacte reeksweergave voor nummerlijsten (#145): [12, 45, 46, 47, 203]
 * wordt "12, 45–47, 203". De set-dekking-pagina toont zo ook lange
 * ontbrekende-nummers-lijsten leesbaar; de API levert de exacte lijst.
 */
export function compactRanges(numbers: number[]): string {
	if (numbers.length === 0) return '';
	const sorted = [...new Set(numbers)].sort((a, b) => a - b);
	const parts: string[] = [];
	let start = sorted[0];
	let prev = sorted[0];
	for (const n of sorted.slice(1)) {
		if (n === prev + 1) {
			prev = n;
			continue;
		}
		parts.push(start === prev ? `${start}` : `${start}–${prev}`);
		start = prev = n;
	}
	parts.push(start === prev ? `${start}` : `${start}–${prev}`);
	return parts.join(', ');
}
