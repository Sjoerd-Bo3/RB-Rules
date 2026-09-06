using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Her-embed over alle vectorlagen na een modelwissel (#382). Vóór #382
/// las alleen de kaartpijplijn <c>EmbeddingModel</c> terug; regels, primer,
/// rulings en claims bleven na een wissel stil op oude vectoren staan.</summary>
public class EmbeddingRefreshServiceTests
{
    private const string Oud = "bge-vorig";

    [Fact]
    public async Task RunAsync_HerembedAlleenRijenMetVectorEnAfwijkendeStempel()
    {
        using var db = NewDb();
        var actueel = EmbeddingConfig.Model;
        // Regels: één met oude stempel (moet), één actueel (mag niet), één zonder
        // vector (hoort bij zijn eigen pijplijn, moet blijven liggen).
        var oudeChunk = new RuleChunk { SourceId = "core", Text = "Deflect prevents damage.", Embedding = Vec(0.5), EmbeddingModel = Oud };
        var actueleChunk = new RuleChunk { SourceId = "core", Text = "Tank redirects attacks.", Embedding = Vec(0.5), EmbeddingModel = actueel, EmbeddingVariant = RuleChunkEmbedText.Variant };
        var legeChunk = new RuleChunk { SourceId = "core", Text = "No vector yet." };
        db.RuleChunks.AddRange(oudeChunk, actueleChunk, legeChunk);
        // Primer met oude stempel.
        var primer = new KnowledgeDoc { Kind = "primer", Topic = "combat", Title = "Combat", Body = "How combat works.", Status = "approved", Embedding = Vec(0.5), EmbeddingModel = Oud };
        db.KnowledgeDocs.Add(primer);
        // Ruling met vector maar ZONDER stempel — het echte productiegeval
        // (drie van de vier schrijfpaden zetten EmbeddingModel nooit).
        var ruling = new Correction { Scope = "card", Ref = "ogn-001", Question = "Can I deflect?", Text = "Yes.", Status = "verified", Embedding = Vec(0.5) };
        db.Corrections.Add(ruling);
        // Claim actueel: mag niet aangeraakt.
        var claim = new Claim { TopicType = "mechanic", TopicRef = "Deflect", Statement = "Deflect stacks.", Embedding = Vec(0.5), EmbeddingModel = actueel };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();

        var aangeboden = new List<string>();
        var svc = Service(db, req =>
        {
            aangeboden.AddRange(Inputs(req));
            return OkEmbeddings(Inputs(req).Count, 0.9);
        });

        var r = await svc.RunAsync();

        Assert.False(r.HasFailures);
        Assert.Equal(3, r.Embedded);
        // Precies de drie stale rijen zijn aangeboden, met de tekstvorm van hun laag
        // (regels: de contextuele vorm van #385 — zonder Source-rij valt de
        // bronnaam terug op het id, zonder sectiecode alleen de bron).
        Assert.Equal(
            ["(core) — Deflect prevents damage.", "Combat\nHow combat works.", "Can I deflect?\nYes."],
            aangeboden);
        Assert.Equal(RuleChunkEmbedText.Variant, oudeChunk.EmbeddingVariant);
        // Ring-A-provenance (#233): de hash van de exacte invoer staat op de rij.
        Assert.Equal(EmbeddingProvenance.ContentHash("(core) — Deflect prevents damage."), oudeChunk.EmbeddingContentHash);
        Assert.Equal(EmbeddingProvenance.ContentHash("Combat\nHow combat works."), primer.EmbeddingContentHash);

        Assert.Equal(actueel, oudeChunk.EmbeddingModel);
        Assert.Equal(0.9f, oudeChunk.Embedding!.ToArray()[0]);
        Assert.Equal(actueel, primer.EmbeddingModel);
        Assert.Equal(actueel, ruling.EmbeddingModel);
        // Onaangeraakt:
        Assert.Equal(0.5f, actueleChunk.Embedding!.ToArray()[0]);
        Assert.Null(legeChunk.Embedding);
        Assert.Equal(0.5f, claim.Embedding!.ToArray()[0]);

        // Herstel-regel per laag mét werk (#282-review), niets voor lagen zonder.
        var logs = await db.RunLogs.Where(l => l.Kind == "embed").OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(["regels", "primer", "rulings"], logs.Select(l => l.Ref).ToList());
        Assert.All(logs, l => Assert.Equal("ok", l.Status));
        Assert.Contains("regels", r.Summary);
        Assert.DoesNotContain("claims", r.Summary);
    }

    [Fact]
    public async Task RunAsync_ZonderWerk_MeldtAlleLagenActueel()
    {
        using var db = NewDb();
        db.RuleChunks.Add(new RuleChunk { SourceId = "core", Text = "x", Embedding = Vec(0.5), EmbeddingModel = EmbeddingConfig.Model, EmbeddingVariant = RuleChunkEmbedText.Variant });
        await db.SaveChangesAsync();
        var geraakt = false;
        var svc = Service(db, _ => { geraakt = true; return OkEmbeddings(1, 0.9); });

        var r = await svc.RunAsync();

        Assert.False(geraakt, "zonder stale rijen hoort Ollama niet aangeraakt te worden");
        Assert.Equal(0, r.Embedded);
        Assert.Equal("alle lagen actueel", r.Summary);
        Assert.Empty(await db.RunLogs.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_OudeInvoervorm_WordtContextueelHerembed_MetBronEnOuders()
    {
        // #385: een chunk met het actuele model maar zónder variant (kale tekst van
        // vóór #385) is stale; de nieuwe invoer draagt §-code, bronnaam en de eerste
        // zin van de ouders uit de bestaande index.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "core", Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
        });
        db.RuleChunks.AddRange(
            new RuleChunk { SourceId = "core", SectionCode = "466", ChunkIndex = 0, Text = "Combat. Units fight here.", Embedding = Vec(0.5), EmbeddingModel = EmbeddingConfig.Model, EmbeddingVariant = RuleChunkEmbedText.Variant },
            new RuleChunk { SourceId = "core", SectionCode = "466.2", ChunkIndex = 1, Text = "Blocking. Declare blockers.", Embedding = Vec(0.5), EmbeddingModel = EmbeddingConfig.Model, EmbeddingVariant = RuleChunkEmbedText.Variant });
        var leaf = new RuleChunk { SourceId = "core", SectionCode = "466.2.c", ChunkIndex = 2, Text = "A blocker must be ready.", Embedding = Vec(0.5), EmbeddingModel = EmbeddingConfig.Model };
        db.RuleChunks.Add(leaf);
        await db.SaveChangesAsync();
        var aangeboden = new List<string>();
        var svc = Service(db, req => { aangeboden.AddRange(Inputs(req)); return OkEmbeddings(Inputs(req).Count, 0.9); });

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Embedded);
        Assert.Equal(["§ 466.2.c (Core Rules) — Combat. — Blocking. — A blocker must be ready."], aangeboden);
        Assert.Equal(RuleChunkEmbedText.Variant, leaf.EmbeddingVariant);
        Assert.Equal("A blocker must be ready.", leaf.Text); // opslag ongewijzigd
    }

    [Fact]
    public async Task RunAsync_OllamaValtOm_LaatStempelStaanEnLogtFout()
    {
        using var db = NewDb();
        var chunk = new RuleChunk { SourceId = "core", Text = "x", Embedding = Vec(0.5), EmbeddingModel = Oud };
        db.RuleChunks.Add(chunk);
        await db.SaveChangesAsync();
        var svc = Service(db, _ => Json(HttpStatusCode.InternalServerError, """{"error":"boom"}"""));

        var r = await svc.RunAsync();

        Assert.True(r.HasFailures);
        Assert.Equal(0, r.Embedded);
        // De oude stempel blijft: de rij komt bij de volgende run gewoon terug.
        Assert.Equal(Oud, chunk.EmbeddingModel);
        Assert.Equal(0.5f, chunk.Embedding!.ToArray()[0]);
        var log = Assert.Single(await db.RunLogs.ToListAsync());
        Assert.Equal("error", log.Status);
        Assert.Equal("regels", log.Ref);
    }

    // ── helpers (zelfde stub-patroon als EmbedOutcomeTests) ─────────────────

    private static Vector Vec(double v) =>
        new(Enumerable.Repeat((float)v, EmbeddingConfig.Dimensions).ToArray());

    private static EmbeddingRefreshService Service(
        RbRulesDbContext db, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(db, new EmbeddingService(
            new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://ollama.test") },
            EmbeddingSettings.Default), EmbeddingSettings.Default);

    private static List<string> Inputs(HttpRequestMessage req)
    {
        var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return [.. System.Text.Json.JsonDocument.Parse(body)
            .RootElement.GetProperty("input").EnumerateArray()
            .Select(e => e.GetString()!)];
    }

    private static HttpResponseMessage OkEmbeddings(int count, double value)
    {
        var one = $"[{string.Join(",", Enumerable.Repeat(value.ToString(System.Globalization.CultureInfo.InvariantCulture), EmbeddingConfig.Dimensions))}]";
        return Json(HttpStatusCode.OK, $$"""{"embeddings":[{{string.Join(",", Enumerable.Repeat(one, count))}}]}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

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
}
