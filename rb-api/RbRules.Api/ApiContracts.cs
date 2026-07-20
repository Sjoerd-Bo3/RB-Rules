using Fido2NetLib;

namespace RbRules.Api;

/// <summary>Beheerder-bewerking van een bron. <c>ContentKind</c> (#188-review,
/// fix C) is de bron-type-override: "faq" | "patch-notes" | "other" zet de
/// classificatie vast met herkomst "admin" (nooit meer geherclassificeerd;
/// telt in de consensus-poort van de patch-notes-retractie als menselijke
/// bevestiging); leeg ("") wist haar — terug naar herclassificatie bij de
/// eerstvolgende scan (zelfde leeg-is-expliciet-wissen-conventie als
/// <see cref="FeedPatch.CategoryFilter"/>); null = niet aanraken.</summary>
public record SourcePatch(
    string? Name, string? Url, short? TrustTier, int? Rank, string? Cadence, bool? Enabled,
    string? ContentKind = null);

/// <summary>Negeren van een bron met reden (#180) — los van <see
/// cref="SourcePatch.Enabled"/>: negeren is een bewuste, blijvende
/// beoordeling ("levert niets op"), Enabled is "tijdelijk uit". Reason is
/// vrije tekst, puur informatief (geen validatie op de inhoud zelf).</summary>
public record SourceIgnoreRequest(string? Reason);

/// <summary>Beheerder-bewerking van een bron-feed (#167). CategoryFilter leeg
/// (""), niet null, betekent expliciet "alle categorieën" (het filter uit).</summary>
public record FeedPatch(
    string? Name, string? Url, string? CategoryFilter, bool? AutoApprove,
    string? Cadence, bool? Enabled);

/// <summary>Beheerder-bewerking van een kennisdoc (#70).
/// <paramref name="BodyNl"/> (#266) is de Nederlandse weergavetekst: leeg
/// meesturen wist haar (de pagina toont dan het Engels), weglaten laat haar
/// staan.</summary>
public record KnowledgePatch(string? Title, string? Body, string? BodyNl = null);

/// <summary>Approach (#153): "auto"|"fast"|"thorough" — alleen gehonoreerd
/// voor een geauthenticeerde vrager (anders genegeerd); onbekende waarden
/// vallen op auto terug. De server-flag blijft de meester.</summary>
public record AskRequest(
    string Question, List<AskImageDto>? Images = null, List<AskTurnDto>? History = null,
    string? Approach = null);

/// <summary>Eerdere ronde in een doorvraag-gesprek (#41).</summary>
public record AskTurnDto(string Question, string Answer);

public record AskImageDto(string MediaType, string Data);

public record ResolveRequest(string[] CardIds);

public record CorrectionSubmit(string Question, string Verdict, string? Text);

public record PushSubscribe(string Endpoint, string P256dh, string Auth);

public record PushUnsubscribe(string Endpoint);

/// <summary>Magic-link-login (#42).</summary>
public record AuthRequestDto(string? Email);

public record AuthVerifyDto(string? Token);

/// <summary>Passkey-registratie (#109): zonder sessie is Email de identifier
/// voor een nieuw account; mét sessie komt de passkey bij het eigen account.</summary>
public record PasskeyRegisterOptionsDto(string? Email);

/// <summary>Verzilvering van een passkey-ceremonie (#109): het challenge-token
/// uit de options-stap plus het (base64url-)antwoord van de authenticator —
/// fido2-net-lib levert de JSON-vorm van Response.</summary>
public record PasskeyRegisterVerifyDto(string? Token, AuthenticatorAttestationRawResponse? Response);

public record PasskeyLoginVerifyDto(string? Token, AuthenticatorAssertionRawResponse? Response);

/// <summary>Beheerder-bewerking van een account (#42): blokkeren en quota
/// (incl. het Grondig-dagquotum, #153).</summary>
public record UserPatch(
    bool? Blocked, int? DailyQuota, int? DailyPhotoQuota, int? DailyAgenticQuota = null);

/// <summary>Review-beslissing op een claim/relatie (#124): optionele
/// beheerder-notitie bij bevestigen, verwerpen of notitie-promotie.</summary>
public record ReviewDecision(string? Note);

/// <summary>Bulk-actie per aanbevelingsgroep op de relatie-reviewqueue (#199
/// v1): Recommendation selecteert de groep ("accept"|"reject"|"unsure"),
/// Decision is wat er met die groep gebeurt ("accept"|"reject").
/// ExpectedCount/AsOf zijn de TOCTOU-fence (review-fix finding 1): wat de UI
/// rendeerde — de groepstelling en de max RecommendedAt binnen de groep.
/// Alle velden nullable + <see cref="ValidationError"/> (review-fix
/// finding 6): een ontbrekend veld is een 400 met een duidelijke fout, geen
/// NRE-500.</summary>
public record RelationBulkDecideRequest(
    string? Recommendation, string? Decision, int? ExpectedCount, DateTimeOffset? AsOf)
{
    /// <summary>null = geldig; anders de mensleesbare 400-fout. Puur en
    /// getest (RelationBulkDecideRequestTests) — het endpoint blijft dun.</summary>
    public string? ValidationError() =>
        Recommendation?.Trim().ToLowerInvariant() is not ("accept" or "reject" or "unsure")
            ? "recommendation moet 'accept', 'reject' of 'unsure' zijn"
            : Decision?.Trim().ToLowerInvariant() is not ("accept" or "reject")
                ? "decision moet 'accept' of 'reject' zijn"
                : ExpectedCount is null or < 0
                    ? "expectedCount ontbreekt of is negatief — stuur de geladen groepstelling mee"
                    : AsOf is null
                        ? "asOf ontbreekt — stuur de geladen groeps-tijdstempel mee"
                        : null;
}

/// <summary>Deck-code-import (#264): de geplakte code, plus optioneel het
/// format waartegen de legaliteit geoordeeld wordt (default "constructed").
/// Beide nullable — een lege body geeft een nette 400 uit
/// <see cref="RbRules.Infrastructure.DeckCodeService.DecodeAsync"/>, geen
/// NRE-500.</summary>
public record DeckDecodeRequest(string? Code, string? Format);

/// <summary>Eén te zetten instelling (#254): een sleutel uit
/// <see cref="RbRules.Domain.ManagedSettingsCatalog"/>. <see cref="Value"/> leeg/null
/// = terug naar de env-/codewaarde (override weg).</summary>
public record SettingPatch(string? Key, string? Value);

/// <summary>Beheerde instellingen zetten (#254). Een LIJST omdat het nachtvenster een
/// paar is: start en eind moeten samen beoordeeld worden, anders strandt een geldige
/// eindtoestand op de tussentoestand. <see cref="Actor"/> is wie het zette; beheer
/// deelt één wachtwoord, dus in de praktijk "beheer".</summary>
public record SettingsPatch(IReadOnlyList<SettingPatch>? Changes, string? Actor);
