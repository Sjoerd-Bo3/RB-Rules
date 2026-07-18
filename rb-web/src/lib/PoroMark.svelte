<script lang="ts">
	// Poracle-mascotte (poro, variant A). Herbruikbaar merkteken: het lijf is
	// crème op elk oppervlak (licht én donker), dus de kleuren lopen via CSS-vars
	// met crème-defaults. Geel hoort NIET in de kuif — dat leeft in de app-tegel
	// en het woordmerk-accent. Geen emoji: pure vectorgeometrie.
	//
	// De vars zijn overschrijfbaar door een ouder-element (of een inline `style`),
	// zodat dezelfde poro als donkere lijn op de gele favicon-tegel kan verschijnen.
	let {
		size = 22,
		label
	}: {
		/** Hoogte in px; de breedte volgt de verhouding 120:124. */
		size?: number;
		/** Optioneel toegankelijk label; zonder label is de poro decoratief. */
		label?: string;
	} = $props();
</script>

<svg
	class="poro"
	viewBox="0 0 120 124"
	height={size}
	width={(size * 120) / 124}
	role={label ? 'img' : undefined}
	aria-label={label}
	aria-hidden={label ? undefined : 'true'}
	focusable="false"
>
	<!-- Voeten -->
	<g fill="var(--poro-line)"><ellipse cx="46" cy="114" rx="12" ry="9" /><ellipse cx="74" cy="114" rx="12" ry="9" /></g>
	<g fill="var(--poro-body)"><ellipse cx="46" cy="112" rx="9.5" ry="7" /><ellipse cx="74" cy="112" rx="9.5" ry="7" /></g>
	<!-- Kuif -->
	<g fill="var(--poro-line)"><circle cx="51" cy="17" r="9.5" /><circle cx="66" cy="13" r="10.5" /></g>
	<g fill="var(--poro-tuft)"><circle cx="51" cy="18" r="6.8" /><circle cx="66" cy="14.5" r="7.6" /></g>
	<!-- Pluizig lijf (donkere omtrek + crème vulling) -->
	<g fill="var(--poro-line)"><circle cx="60" cy="66" r="43" /><circle cx="38" cy="44" r="23" /><circle cx="60" cy="34" r="25" /><circle cx="82" cy="44" r="23" /><circle cx="26" cy="66" r="23" /><circle cx="94" cy="66" r="23" /><circle cx="40" cy="92" r="23" /><circle cx="80" cy="92" r="23" /><circle cx="60" cy="98" r="27" /></g>
	<g fill="var(--poro-body)"><circle cx="60" cy="66" r="40" /><circle cx="38" cy="44" r="20" /><circle cx="60" cy="34" r="22" /><circle cx="82" cy="44" r="20" /><circle cx="26" cy="66" r="20" /><circle cx="94" cy="66" r="20" /><circle cx="40" cy="92" r="20" /><circle cx="80" cy="92" r="20" /><circle cx="60" cy="98" r="24" /></g>
	<!-- Wangen -->
	<circle cx="34" cy="74" r="6" fill="var(--poro-cheek)" /><circle cx="86" cy="74" r="6" fill="var(--poro-cheek)" />
	<!-- Ogen + glimlichtjes -->
	<circle cx="48" cy="62" r="7.6" fill="var(--poro-eye)" /><circle cx="72" cy="62" r="7.6" fill="var(--poro-eye)" />
	<circle cx="50.6" cy="58.8" r="2.5" fill="#fff" /><circle cx="74.6" cy="58.8" r="2.5" fill="#fff" />
	<!-- Tong + mond -->
	<path d="M54 80 Q54 97 60 97 Q66 97 66 80 Z" fill="var(--poro-tongue)" stroke="var(--poro-line)" stroke-width="2.4" />
	<line x1="60" y1="84" x2="60" y2="94" stroke="var(--poro-line)" stroke-width="1.8" stroke-linecap="round" opacity=".45" />
	<path d="M49 79 Q60 85 71 79" fill="none" stroke="var(--poro-eye)" stroke-width="3" stroke-linecap="round" />
</svg>

<style>
	.poro {
		/* Crème lijf in BEIDE thema's; overschrijfbaar per gebruik. */
		--poro-body: #f4f1ea;
		--poro-line: #2a2e37;
		--poro-eye: #23262e;
		--poro-tongue: #ef8a9a;
		--poro-cheek: #f3b0aa;
		--poro-tuft: #f4f1ea;
		display: inline-block;
		flex: none;
		vertical-align: middle;
	}
</style>
