// Uitleg bij de quota-/sessiepoort van rb-api (#42). Eén plek voor de
// teksten: de niet-streamende action (+page.server.ts) en het streamingpad
// (+page.svelte) moeten dezelfde uitleg geven bij dezelfde status.
export function quotaMessage(status: number): string | null {
	if (status === 429)
		return 'Limiet bereikt: te veel vragen in korte tijd of je dagquotum is op. Probeer het later opnieuw — of log in via Account voor een ruimer quotum.';
	if (status === 401) return 'Je sessie is verlopen — log opnieuw in via Account.';
	if (status === 403) return 'Dit account is geblokkeerd door de beheerder.';
	return null;
}
