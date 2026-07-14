namespace RbRules.Domain;

/// <summary>Stand van de <c>ASK_AGENTIC</c>-feature-flag (#107,
/// docs/BRAIN.md §2.4). Default is <see cref="Off"/>: de deploy verandert
/// het live gedrag niet totdat de beheerder de flag omzet.</summary>
public enum AgenticMode
{
    /// <summary>Nooit escaleren — het standaardpad (single-pass).</summary>
    Off,
    /// <summary>Escaleren wanneer de vraag kwalificeert (§2.4-triggers).</summary>
    Auto,
    /// <summary>Altijd escaleren — bestaat alleen voor verificatie.</summary>
    Force,
}

/// <summary>Aanpak-keuze per vraag (#153): een ingelogde vrager mag de gate
/// sturen. <see cref="Auto"/> = de bestaande gate beslist (en de enige stand
/// voor anonieme vragen), <see cref="Fast"/> = nooit escaleren (geforceerde
/// single-pass), <see cref="Thorough"/> = de brein-agent forceren — binnen
/// het eigen dagquotum en altijd ondergeschikt aan de server-flag.</summary>
public enum AskApproach
{
    Auto,
    Fast,
    Thorough,
}

/// <summary>Wie de escalatie-uitkomst bepaalde (#153) — voor de trace-badge
/// ("agentic (gate)" vs "agentic (gebruiker)") en de UI-melding bij
/// quota-terugval.</summary>
public enum AskDecider
{
    /// <summary>De automatische gate (§2.4-triggers of de flag).</summary>
    Gate,
    /// <summary>De gebruiker: een gehonoreerde Snel- of Grondig-keuze.</summary>
    User,
    /// <summary>Grondig gevraagd, maar het dagquotum is op — teruggevallen
    /// op Auto zonder dat de gate zelf escaleerde.</summary>
    QuotaFallback,
}

/// <summary>Uitkomst van <see cref="AgenticGate.Decide"/>: escaleren of niet,
/// wie dat besliste en — als de gebruikerskeuze niet gehonoreerd is — waarom
/// (een van de Reason-constanten op <see cref="AgenticGate"/>).</summary>
public record AgenticDecision(
    bool Escalate, AskDecider DecidedBy, string? FallbackReason = null);

/// <summary>Gate voor agentic ask (#107): beslist ná de normale retrieval of
/// een vraag mag door-redeneren over het brein. Puur en unit-getest — de
/// I/O-kant (env lezen, retrieval-signalen verzamelen) blijft in AskService.</summary>
public static class AgenticGate
{
    /// <summary>Terugval-redenen (#153) zoals ze in respons-metadata reizen;
    /// de UI vertaalt ze naar een melding ("quota op — automatisch
    /// beantwoord").</summary>
    public const string ReasonDisabled = "disabled";
    public const string ReasonPhoto = "photo";
    public const string ReasonQuota = "quota";

    /// <summary>Parse de <c>ASK_AGENTIC</c>-env-waarde. Onbekend, leeg of
    /// afwezig valt op <see cref="AgenticMode.Off"/> terug: een tikfout mag
    /// nooit stilzwijgend het dure agent-pad aanzetten (zelfde principe als
    /// de task-fallback in rb-ai's validate.ts).</summary>
    public static AgenticMode ParseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "auto" => AgenticMode.Auto,
            "force" => AgenticMode.Force,
            _ => AgenticMode.Off,
        };

    /// <summary>Parse het <c>approach</c>-request-veld (#153). Onbekend, leeg
    /// of afwezig valt op <see cref="AskApproach.Auto"/> terug — zelfde
    /// principe als <see cref="ParseMode"/>: een tikfout mag nooit
    /// stilzwijgend het dure pad forceren.</summary>
    public static AskApproach ParseApproach(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "fast" => AskApproach.Fast,
            "thorough" => AskApproach.Thorough,
            _ => AskApproach.Auto,
        };

    /// <summary>De volledige escalatie-beslissing (#153): gebruikerskeuze +
    /// flag + vraagsignalen + quota-stand → uitkomst mét attributie.
    /// - <see cref="AskApproach.Fast"/> escaleert nooit, ook niet onder
    ///   <c>force</c> — de gebruiker koos expliciet voor de snelle pass.
    /// - <see cref="AskApproach.Thorough"/> escaleert mits de flag niet
    ///   <c>off</c> is (de flag blijft de meester: zonder <c>ASK_AGENTIC</c>
    ///   bestaat Grondig niet, ook niet via de API), er geen foto bij zit
    ///   (de bestaande gate-regel: vision blijft het Opus-pad) én het
    ///   dagquotum nog ruimte heeft — anders valt de vraag terug op Auto,
    ///   met de reden in <see cref="AgenticDecision.FallbackReason"/>.
    /// - <see cref="AskApproach.Auto"/> is exact het bestaande
    ///   <see cref="ShouldEscalate"/>-gedrag.
    /// Alleen een gehonoreerde keuze krijgt <see cref="AskDecider.User"/>:
    /// gate-escalaties (ook ná een terugval) blijven op de gate geboekt en
    /// tellen dus niet tegen het gebruikersquotum.</summary>
    public static AgenticDecision Decide(
        AskApproach approach, AgenticMode mode, QuestionType type, int cardMentions,
        bool emptyRetrieval, bool hasImage, bool quotaAvailable)
    {
        var auto = ShouldEscalate(type, cardMentions, emptyRetrieval, mode, hasImage);
        return approach switch
        {
            AskApproach.Fast => new(false, AskDecider.User),
            // Volgorde van de terugval-redenen: de flag is de meester, de
            // foto-regel is een harde gate-regel, quota komt als laatste —
            // zo krijgt de gebruiker altijd de meest fundamentele reden.
            AskApproach.Thorough when mode == AgenticMode.Off =>
                new(auto, AskDecider.Gate, ReasonDisabled),
            AskApproach.Thorough when hasImage =>
                new(auto, AskDecider.Gate, ReasonPhoto),
            AskApproach.Thorough when !quotaAvailable =>
                new(auto, auto ? AskDecider.Gate : AskDecider.QuotaFallback, ReasonQuota),
            // Zou de gate deze vraag tóch al escaleren (auto == true, bv.
            // Ruling met ≥2 kaartnamen of onder Force), dan is de escalatie
            // gratis — net als bij approach=Auto — en boekt hij op de gate, niet
            // op de gebruiker: hij mag dan geen Grondig-quotum kosten.
            AskApproach.Thorough => new(true, auto ? AskDecider.Gate : AskDecider.User),
            _ => new(auto, AskDecider.Gate),
        };
    }

    /// <summary>Welke aanpak het wérd, als respons-metadata-sleutel (#153):
    /// alleen een gehonoreerde gebruikerskeuze meldt zich als fast/thorough —
    /// elke terugval is eerlijk "auto" (plus de reden ernaast).</summary>
    public static string EffectiveApproach(AgenticDecision decision, AskApproach requested) =>
        decision.DecidedBy == AskDecider.User && requested == AskApproach.Fast ? "fast"
        : decision.DecidedBy == AskDecider.User && requested == AskApproach.Thorough ? "thorough"
        : "auto";

    /// <summary>§2.4: in <c>auto</c> kwalificeert een vraag alléén als
    /// (a) vraagtype Ruling met ≥2 herkende kaartnamen in de huidige vraag —
    /// interactievragen, precies waar één pass aantoonbaar context mist — óf
    /// (b) het lege-retrieval-signaal (zie de berekening in AskService).
    /// <c>force</c> escaleert altijd (verificatie), <c>off</c> nooit.
    /// Foto-vragen escaleren nooit, óók niet onder force (review #107):
    /// board-state-analyse kreeg bewust het Opus-visionpad (task "hard") en
    /// de brein-tools zijn tekst-only — escaleren zou die keuze stil
    /// downgraden naar het Sonnet-agentpad.</summary>
    public static bool ShouldEscalate(
        QuestionType type, int cardMentions, bool emptyRetrieval, AgenticMode mode,
        bool hasImage = false)
    {
        if (hasImage) return false;
        return mode switch
        {
            AgenticMode.Force => true,
            AgenticMode.Auto =>
                (type == QuestionType.Ruling && cardMentions >= 2) || emptyRetrieval,
            _ => false,
        };
    }

    /// <summary>Telt hoeveel écht verschillende kaarten er genoemd zijn
    /// (review #107). De naam-match in AskService is een substring-match:
    /// "Jinx" matcht óók binnen "Jinx, Loose Cannon", waardoor één genoemde
    /// kaart als twee mentions zou tellen en een enkelkaart-Ruling onterecht
    /// zou escaleren. Namen die deel zijn van een langere gematchte naam
    /// tellen daarom niet mee.</summary>
    public static int CountDistinctMentions(IReadOnlyCollection<string> matchedNames)
    {
        var distinct = matchedNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count(name => !distinct.Any(other =>
            other.Length > name.Length &&
            other.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }
}
