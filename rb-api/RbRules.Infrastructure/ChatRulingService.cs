using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Wie de ruling indient — bepaalt de route (#166, de veiligheidskern):
/// een beheerder verifieert direct, een ingelogde gebruiker legt een voorstel
/// vast in de reviewqueue. Anoniem komt hier nooit binnen (de Api-laag wijst
/// dat af vóórdat de service wordt aangeroepen).</summary>
public enum RulingAuthority { Admin, User }

public enum RulingSubmitStatus { Verified, Pending, InvalidInput }

public record RulingSubmit(
    string Statement, string Scope, string? TopicRef, string SourceRef, string? Question);

public record RulingSubmitResult(
    RulingSubmitStatus Status, string? Error = null,
    long CorrectionId = 0, bool Embedded = false, bool Updated = false);

/// <summary>In-chat ruling vastleggen vanuit /ask (#166): dezelfde Correction-
/// infrastructuur als ReviewNoteService (#124) — scope + BrainRef-achtige
/// verwijzing + tekst + vraag + provenance + embedding — maar dan aangestuurd
/// vanuit een gesprek in plaats van een reviewqueue-notitie.
///
/// Autoriteit bepaalt de route (de kennislagen-regel, docs/KNOWLEDGE.md): een
/// beheerder (RulingAuthority.Admin) verifieert direct via hetzelfde
/// verify-pad als admin/corrections/{id}/verify — telt meteen mee in /ask en
/// /rulings. Een ingelogde gebruiker (RulingAuthority.User) legt alleen een
/// voorstel vast (status unverified): dat voorstel wordt NOOIT hier
/// geverifieerd of geëmbed — het bestaande admin-verify-pad is de enige weg
/// naar "telt mee". Dat is de anti-vergiftigingsgrens: een kwaadwillende
/// ingelogde gebruiker kan nooit zelf antwoorden sturen.
///
/// Bron ("waar besloten") is verplicht — een ruling zonder herkomst is geen
/// ruling. URL's gaan door UrlGuard (SSRF, #45); een vrije citatie (Discord-
/// thread, toernooi + datum) blijft tekst — sanitize gebeurt bij weergave.
///
/// Idempotent per (scope, onderwerp, exacte uitspraak): dezelfde ruling nog
/// eens indienen stapelt niet, maar werkt de bestaande rij bij (nieuwe bron,
/// nieuwe context) — en een beheerder die exact dezelfde tekst indient als al
/// een pending voorstel bestaat, promoveert dat voorstel in plaats van een
/// tweede rij te maken.</summary>
public class ChatRulingService(RbRulesDbContext db, EmbeddingService embeddings)
{
    private static readonly string[] ValidScopes = ["card", "rule_section", "answer"];
    private const int MaxStatementLength = 4000;
    private const int MaxSourceRefLength = 2000;
    private const int MaxQuestionLength = 2000;
    private const int SlugLength = 80;

    public async Task<RulingSubmitResult> SubmitAsync(
        RulingSubmit body, RulingAuthority authority, CancellationToken ct = default)
    {
        // De verplichte velden zijn non-nullable in RulingSubmit, maar
        // System.Text.Json handhaaft dat niet: een ontbrekend of expliciet
        // null JSON-veld bindt naar null. Guard vóór elke .Trim() (patroon
        // AskEndpoints.ValidateAsk) zodat een kwaadaardige/slordige body een
        // nette InvalidInput/400 geeft in plaats van een NullReferenceException
        // → kale 500.
        if (string.IsNullOrWhiteSpace(body.Scope))
            return Invalid("onbekende scope — kies card, rule_section of answer");
        var scope = body.Scope.Trim().ToLowerInvariant();
        if (Array.IndexOf(ValidScopes, scope) < 0)
            return Invalid("onbekende scope — kies card, rule_section of answer");

        if (string.IsNullOrWhiteSpace(body.Statement))
            return Invalid("de uitspraak ontbreekt");
        var statement = body.Statement.Trim();
        if (statement.Length > MaxStatementLength)
            return Invalid($"de uitspraak is te lang (max {MaxStatementLength} tekens)");

        if (string.IsNullOrWhiteSpace(body.SourceRef))
            return Invalid("een bronverwijzing (waar besloten) is verplicht — een ruling zonder herkomst is geen ruling");
        var sourceRef = body.SourceRef.Trim();
        if (sourceRef.Length > MaxSourceRefLength)
            return Invalid($"de bronverwijzing is te lang (max {MaxSourceRefLength} tekens)");
        // Alleen als absolute URL door UrlGuard (SSRF, #45) — een vrije
        // citatie ("Discord #rulings, 2026-05-01") is evengoed een geldige bron.
        if (Uri.TryCreate(sourceRef, UriKind.Absolute, out _)
            && UrlGuard.Check(sourceRef) is { Allowed: false } blocked)
            return Invalid($"bron-URL geweigerd: {blocked.Reason}");

        var topicRef = body.TopicRef?.Trim();
        if (scope is "card" or "rule_section" && string.IsNullOrEmpty(topicRef))
            return Invalid(scope == "card"
                ? "kies een kaart voor deze scope"
                : "kies een §-sectie voor deze scope");
        // "answer" (algemeen) heeft geen natuurlijk onderwerp — een stabiele
        // slug van de uitspraak zelf volstaat voor de idempotentie hieronder
        // zonder een nep-onderwerp te verzinnen.
        var reference = !string.IsNullOrEmpty(topicRef) ? topicRef : Slug(statement);

        var question = body.Question?.Trim();
        if (question is { Length: > MaxQuestionLength }) question = question[..MaxQuestionLength];

        var existing = await db.Corrections.FirstOrDefaultAsync(
            c => c.Scope == scope && c.Ref == reference && c.Text == statement, ct);

        if (authority == RulingAuthority.User && existing is { Status: "verified" })
        {
            // Al geverifieerd met exact dezelfde tekst: niets te doen — geen
            // tweede (pending) rij naast een al-geldige ruling.
            return new(RulingSubmitStatus.Pending, CorrectionId: existing.Id, Updated: true);
        }

        var updated = existing is not null;
        var correction = existing ?? new Correction { Scope = scope, Ref = reference, Text = statement };
        if (existing is null) db.Corrections.Add(correction);

        correction.Text = statement;
        correction.Question = question;
        correction.SourceRef = sourceRef;
        correction.Provenance = authority == RulingAuthority.Admin ? "chat-ruling:admin" : "chat-ruling:user";

        if (authority == RulingAuthority.User)
        {
            // De anti-vergiftigingsgrens: nooit verified, nooit geëmbed hier.
            // Pas het bestaande verify-pad (admin/corrections/{id}/verify)
            // maakt dit voorstel geldig en doorzoekbaar.
            correction.Status = "unverified";
            await db.SaveChangesAsync(ct);
            return new(RulingSubmitStatus.Pending, CorrectionId: correction.Id, Updated: updated);
        }

        correction.Status = "verified";
        correction.VerifiedAt = DateTimeOffset.UtcNow;
        var embedded = true;
        try
        {
            // Zelfde embed-input als corrections/verify en ReviewNoteService,
            // zodat /ask de ruling semantisch vindt.
            correction.Embedding = await embeddings.EmbedOneAsync($"{question}\n{statement}", ct);
        }
        catch
        {
            // Ollama tijdelijk weg (#100) — verificatie telt, embedding komt
            // bij een volgende her-verify (liever geen embedding dan een
            // stille mismatch met verouderde tekst).
            correction.Embedding = null;
            embedded = false;
        }
        await db.SaveChangesAsync(ct);
        return new(RulingSubmitStatus.Verified, CorrectionId: correction.Id, Embedded: embedded, Updated: updated);
    }

    private static RulingSubmitResult Invalid(string error) => new(RulingSubmitStatus.InvalidInput, error);

    /// <summary>Stabiele, korte sleutel voor topic-vrije ("algemeen") rulings:
    /// dedupe op exact dezelfde uitspraak zonder een onderwerp te faken.</summary>
    private static string Slug(string statement) =>
        (statement.Length > SlugLength ? statement[..SlugLength] : statement)
        .Trim().ToLowerInvariant();
}
