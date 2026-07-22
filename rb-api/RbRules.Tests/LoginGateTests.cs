using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RbRules.Api;
using RbRules.Api.Endpoints;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Login-poort op de AI-paden (#328). Dit zijn bewust ENDPOINT-tests
/// tegen de écht gemapte routes (MapAskEndpoints/MapCardEndpoints), niet alleen
/// unit-tests op de filterklasse: een filter die je van het endpoint kunt halen
/// zonder rode test is geen poort (#292). Mutatie-bewijs: haal
/// <c>AddEndpointFilter&lt;UserQuotaFilter.RequireUser&gt;()</c> van een route en
/// de bijbehorende anoniem-test hier wordt rood (het request bereikt dan de
/// handler en eindigt in een 200/400 in plaats van de 401).</summary>
public class LoginGateTests
{
    /// <summary>Stub-backend voor RbAiClient/EmbeddingService: elke uitgaande
    /// call faalt met 500 — AI-uitval is het bestaande degradatiepad, dus een
    /// doorgelaten request eindigt gewoon in een 200 met de nette
    /// niet-beschikbaar-tekst (precies wat de mutatie-test rood maakt).</summary>
    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Pgvector.Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(
                            new Microsoft.EntityFrameworkCore.Storage.ValueConversion
                                .ValueConverter<Pgvector.Vector, string>(
                                v => v.ToString(), s => new Pgvector.Vector(s)));
        }
    }

    /// <summary>Zonder deze feature ziet minimal-API's request-delegate een
    /// kale DefaultHttpContext als "kan geen body hebben" en eindigt élke
    /// JSON-POST als bodyloze 400 — mét de filters gewoon uitgevoerd, dus een
    /// anoniem-test blijft dan vals-groen op de verkeerde statuscode. De
    /// feature expliciet zetten laat de body-binding echt draaien.</summary>
    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
    }

    private static WebApplication BuildApp(string dbName)
    {
        var builder = WebApplication.CreateSlimBuilder();
        var services = builder.Services;
        services.AddLogging();
        // InMemory kent het pgvector-type niet: dezelfde tekst-conversie als
        // de andere InMemory-tests, hier via een scoped factory zodat élk
        // request-scope zijn eigen context op dezelfde store krijgt.
        services.AddScoped<RbRulesDbContext>(_ => new InMemoryDbContext(
            new DbContextOptionsBuilder<RbRulesDbContext>()
                .UseInMemoryDatabase(dbName).Options));
        services.AddScoped<RequestUserContext>();
        services.AddSingleton<MailService>();
        services.AddScoped<UserAccountService>();
        services.AddSingleton(new EmbeddingService(
            new HttpClient(new FailingHandler()) { BaseAddress = new Uri("http://ollama.test") }));
        services.AddSingleton(new RbAiClient(
            new HttpClient(new FailingHandler()) { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance));
        services.AddSingleton<Neo4j.Driver.IDriver>(new RecordingDriver());
        services.AddScoped<CardResolver>();
        services.AddScoped<CardDetailService>();
        services.AddScoped<CardSimilarityService>();
        services.AddScoped<SimilarityExplainService>();
        services.AddScoped<InteractionService>();
        services.AddScoped<GraphQueryService>();
        services.AddScoped<BrainService>();
        services.AddScoped<AgenticRelationService>();
        services.AddScoped<AskService>();
        services.AddScoped<AskHistoryService>();
        services.AddScoped<ChatRulingService>();

        var app = builder.Build();
        app.MapAskEndpoints();
        app.MapCardEndpoints();
        return app;
    }

    private static RouteEndpoint FindEndpoint(WebApplication app, string method, string pattern) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern
                && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    .Contains(method) ?? false));

    private static async Task<(int Status, string Body)> InvokeAsync(
        WebApplication app, string method, string pattern, string? jsonBody = null,
        IDictionary<string, object?>? routeValues = null, string? userToken = null)
    {
        var endpoint = FindEndpoint(app, method, pattern);
        await using var scope = app.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Method = method;
        if (jsonBody is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            http.Request.ContentType = "application/json";
            http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(
                new BodyDetection());
            http.Request.ContentLength = bytes.Length;
            http.Request.Body = new MemoryStream(bytes);
        }
        if (routeValues is not null)
            foreach (var (k, v) in routeValues) http.Request.RouteValues[k] = v?.ToString();
        if (userToken is not null)
            http.Request.Headers[UserQuotaFilter.TokenHeader] = userToken;
        var responseBody = new MemoryStream();
        http.Response.Body = responseBody;

        await endpoint.RequestDelegate!(http);

        responseBody.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(responseBody).ReadToEndAsync());
    }

    private static async Task<string> SeedLoggedInUserAsync(WebApplication app)
    {
        var token = Accounts.NewToken();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RbRulesDbContext>();
        var user = new AppUser { Email = "speler@example.com" };
        db.Users.Add(user);
        db.UserSessions.Add(new UserSession
        {
            User = user,
            TokenHash = Accounts.HashToken(token),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();
        return token;
    }

    [Theory]
    [InlineData("POST", "/api/ask", """{"question":"Wat doet [Deflect]?"}""")]
    [InlineData("POST", "/api/ask/stream", """{"question":"Wat doet [Deflect]?"}""")]
    [InlineData("POST", "/api/resolve", """{"cardIds":["ogn-1","ogn-2"]}""")]
    public async Task Anoniem_wordt_op_elk_AI_pad_geweigerd(string method, string pattern, string body)
    {
        var app = BuildApp(Guid.NewGuid().ToString());
        var (status, responseBody) = await InvokeAsync(app, method, pattern, body);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        // Wire-waarde als LITERAL (#286-les): een assert tegen de
        // productie-constante schuift mee met een hernoeming.
        Assert.Contains("login_required", responseBody);
    }

    [Fact]
    public async Task Anoniem_wordt_op_de_similarity_uitleg_geweigerd()
    {
        var app = BuildApp(Guid.NewGuid().ToString());
        var (status, body) = await InvokeAsync(
            app, "GET", "/api/cards/{id}/similar/{otherId}/explain",
            routeValues: new Dictionary<string, object?> { ["id"] = "a", ["otherId"] = "b" });
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Contains("login_required", body);
    }

    [Fact]
    public async Task Ingelogd_passeert_de_poort_en_krijgt_gewoon_antwoord()
    {
        // Regressie op het ingelogde pad: mét geldig sessietoken komt de vraag
        // door de poort heen tot in AskService, die op AI-uitval (stub-backend)
        // netjes degradeert naar een 200 met de niet-beschikbaar-tekst.
        var app = BuildApp(Guid.NewGuid().ToString());
        var token = await SeedLoggedInUserAsync(app);
        var (status, body) = await InvokeAsync(
            app, "POST", "/api/ask", """{"question":"Wat doet [Deflect]?"}""", userToken: token);
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("answer", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verlopen_sessie_blijft_de_bestaande_401_van_de_quota_filter()
    {
        // De poort mag de "sessie verlopen"-melding niet maskeren: een kapot/
        // verlopen token wordt al door UserQuotaFilter geweigerd, zónder de
        // login_required-code (rb-web toont daar "log opnieuw in").
        var app = BuildApp(Guid.NewGuid().ToString());
        var (status, body) = await InvokeAsync(
            app, "POST", "/api/ask", """{"question":"test"}""", userToken: "verlopen-token");
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.DoesNotContain("login_required", body);
    }

    [Fact]
    public async Task Anoniem_prewarm_wordt_geweigerd()
    {
        // Review #328: prewarm boot een SDK-subprocess op de VM; anoniem kan
        // toch geen vraag meer stellen, dus ook dit pad zit achter de poort —
        // server-side, niet alleen de rb-web-conditie.
        var app = BuildApp(Guid.NewGuid().ToString());
        var (status, body) = await InvokeAsync(app, "POST", "/api/ask/prewarm");
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Contains("login_required", body);
    }

    [Fact]
    public async Task Ingelogd_prewarm_blijft_werken()
    {
        var app = BuildApp(Guid.NewGuid().ToString());
        var token = await SeedLoggedInUserAsync(app);
        var (status, _) = await InvokeAsync(app, "POST", "/api/ask/prewarm", userToken: token);
        Assert.Equal(StatusCodes.Status202Accepted, status);
    }

    [Fact]
    public async Task Ask_geschiedenis_blijft_anoniem_toegankelijk()
    {
        // De poort dekt alléén AI-paden; de geschiedenis (eigen ip-hash-scope,
        // geen LLM-call) blijft open — de rest van de site verandert niet.
        var app = BuildApp(Guid.NewGuid().ToString());
        var (status, _) = await InvokeAsync(app, "GET", "/api/ask/history");
        Assert.Equal(StatusCodes.Status200OK, status);
    }
}
