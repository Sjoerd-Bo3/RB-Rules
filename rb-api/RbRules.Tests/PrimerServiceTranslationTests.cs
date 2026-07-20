using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De vertaalstap van de primer door de échte pijplijn (#266):
/// rb-ai is de echte RbAiClient op een gestubde handler, zodat prompt,
/// LLM-antwoord en speltermen-waarborg samen getest worden. De database is EF
/// InMemory en blijft leeg — deze test raakt bewust alleen TranslateAsync
/// (de rest van GenerateAsync leunt op pgvector-CosineDistance, alleen
/// Postgres).</summary>
public class PrimerServiceTranslationTests
{
    private const string English =
        "Runes pay for cards (§201.1). In a showdown a Unit deals damage equal "
        + "to its Might (§402.3), and Battlefields are held to score points.";

    [Fact]
    public async Task TranslateAsync_SpeltermenKomenOnvertaaldDoorDePijplijn()
    {
        const string dutch =
            "  Met Runes betaal je kaarten (§201.1). In een showdown deelt een "
            + "Unit schade gelijk aan zijn Might (§402.3), en Battlefields houd "
            + "je vast om punten te scoren.  ";

        var sent = new List<string>();
        var svc = Service(Ai(dutch, sent));
        var result = await svc.TranslateAsync(English);

        Assert.NotNull(result);
        Assert.Equal(dutch.Trim(), result);
        foreach (var term in new[] { "Runes", "showdown", "Unit", "Might", "Battlefields" })
            Assert.Contains(term, result, StringComparison.Ordinal);

        // Het glossarium gaat écht mee naar de sidecar — de waarborg begint
        // bij de instructie, niet pas bij de controle achteraf.
        var payload = Assert.Single(sent);
        foreach (var term in PrimerTranslation.Glossary)
            Assert.Contains(term, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_VernederlandsteSpelterm_WordtAfgekeurd()
    {
        // De waarborg: liever de canonieke Engelse tekst op /primer dan
        // "slagvelden" naast een §-citaat.
        const string dutch =
            "Met runen betaal je kaarten (§201.1). In een krachtmeting deelt een "
            + "eenheid schade gelijk aan zijn kracht (§402.3), en slagvelden "
            + "houd je vast om punten te scoren.";

        var svc = Service(Ai(dutch));

        Assert.Null(await svc.TranslateAsync(English));
    }

    [Fact]
    public async Task TranslateAsync_RbAiWeg_GeeftNull_ZonderCrash()
    {
        // AI-uitval is een verwacht pad (CONVENTIONS): geen vertaling, de
        // pagina toont het Engels.
        var svc = Service(Ai(null));

        Assert.Null(await svc.TranslateAsync(English));
    }

    private static PrimerService Service(RbAiClient ai) => new(
        NewDb(), Embeddings(), ai, NullLogger<PrimerService>.Instance);

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: vectors als tekst
    /// (zelfde patroon als ClaimMiningServiceTests).</summary>
    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new ValueConverter<Vector, string>(
                            v => v.ToString(), s => new Vector(s)));
        }
    }

    /// <summary>Echte RbAiClient op een gestubde sidecar: null ⇒ 500 (uitval),
    /// anders het gegeven antwoord. De verstuurde payload gaat in
    /// <paramref name="sent"/> — zo bewijzen we dat het glossarium écht
    /// meegaat naar rb-ai.</summary>
    private static RbAiClient Ai(string? answer, List<string>? sent = null) => new(
        new HttpClient(new StubHandler(req =>
        {
            sent?.Add(req.Content?.ReadAsStringAsync().Result ?? "");
            return answer is { } a
                ? Json(new { answer = a })
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Ollama is hier niet in beeld: de vertaalstap embedt niets.</summary>
    private static EmbeddingService Embeddings() => new(
        new HttpClient(new StubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
