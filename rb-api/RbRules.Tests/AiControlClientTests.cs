using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RbRules.Infrastructure;

namespace RbRules.Tests;

public class AiControlClientTests
{
    private const string ControlKey = "control-key-that-must-never-leak";

    [Fact]
    public async Task Snapshot_SendsDedicatedControlHeader_AndParsesSafeContract()
    {
        HttpMethod? method = null;
        string? path = null;
        string? header = null;
        var client = Client(async (request, _) =>
        {
            method = request.Method;
            path = request.RequestUri?.AbsolutePath;
            header = request.Headers.GetValues(AiControlClient.ControlHeaderName).Single();
            return Json(HttpStatusCode.OK, SnapshotJson);
        });

        var result = await client.GetSnapshotAsync();

        Assert.True(result.Ok);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("/control", path);
        Assert.Equal(ControlKey, header);
        Assert.Equal(7, result.Value!.Generation);
        Assert.Equal("codex", Assert.Single(result.Value.Models).Alias);
        var provider = Assert.Single(result.Value.Providers);
        Assert.Equal("ready", provider.Status);
        Assert.True(provider.Configured);
    }

    [Fact]
    public async Task CreateAccount_ForwardsJsonExactly_ButNeverEchoesUnknownSecretFields()
    {
        const string credential = "oauth-secret-forwarded-once-only-123456";
        string? forwarded = null;
        var client = Client(async (request, ct) =>
        {
            forwarded = await request.Content!.ReadAsStringAsync(ct);
            return Json(HttpStatusCode.Created, $$"""
                {
                  "account": {
                    "id": "account-1",
                    "poolId": "pool-1",
                    "label": "Claude primary",
                    "enabled": true,
                    "authType": "oauth-token",
                    "status": "unknown",
                    "lastTestedAt": null,
                    "credentialConfigured": true,
                    "editable": true,
                    "credential": "{{credential}}",
                    "accessToken": "{{credential}}"
                  }
                }
                """);
        });
        var request = new AiAccountCreateRequest
        {
            PoolId = "pool-1",
            Label = "Claude primary",
            AuthType = "oauth-token",
            Enabled = true,
            Credential = credential,
        };

        var result = await client.CreateAccountAsync(request);

        Assert.True(result.Ok);
        using var body = JsonDocument.Parse(forwarded!);
        Assert.Equal("pool-1", body.RootElement.GetProperty("poolId").GetString());
        Assert.Equal("Claude primary", body.RootElement.GetProperty("label").GetString());
        Assert.Equal("oauth-token", body.RootElement.GetProperty("authType").GetString());
        Assert.Equal(credential, body.RootElement.GetProperty("credential").GetString());

        var safeResponse = JsonSerializer.Serialize(result.Value, WebJson);
        Assert.DoesNotContain(credential, safeResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("credential\"", safeResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", safeResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(credential, request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceCredential_UpstreamErrorBodyAndExceptionTextAreNeverLoggedOrReturned()
    {
        const string credential = "replacement-secret-987654321";
        var logger = new CapturingLogger<AiControlClient>();
        var client = Client((_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest,
            $$"""{"error":"invalid credential {{credential}}"}""")), logger);

        var result = await client.ReplaceCredentialAsync("account-1",
            new AiCredentialReplaceRequest { Credential = credential });

        Assert.False(result.Ok);
        Assert.Equal(AiControlFailure.InvalidRequest, result.Failure);
        Assert.DoesNotContain(credential, JsonSerializer.Serialize(result, WebJson),
            StringComparison.Ordinal);
        Assert.DoesNotContain(credential, string.Join('\n', logger.Messages),
            StringComparison.Ordinal);

        var throwingLogger = new CapturingLogger<AiControlClient>();
        var throwing = Client((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(
                $"transport copied {credential}")), throwingLogger);

        var unavailable = await throwing.ReplaceCredentialAsync("account-1",
            new AiCredentialReplaceRequest { Credential = credential });

        Assert.Equal(AiControlFailure.Unavailable, unavailable.Failure);
        Assert.DoesNotContain(credential, string.Join('\n', throwingLogger.Messages),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingControlKey_FailsClosedWithoutNetworkCall()
    {
        var called = false;
        var client = new AiControlClient(
            new HttpClient(new StubHandler((_, _) =>
            {
                called = true;
                return Task.FromResult(Json(HttpStatusCode.OK, SnapshotJson));
            })) { BaseAddress = new Uri("http://rb-ai.test") },
            new AiControlOptions(null),
            new CapturingLogger<AiControlClient>());

        var result = await client.GetSnapshotAsync();

        Assert.False(result.Ok);
        Assert.Equal(AiControlFailure.Unavailable, result.Failure);
        Assert.False(called);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, AiControlFailure.InvalidRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity, AiControlFailure.InvalidRequest)]
    [InlineData(HttpStatusCode.NotFound, AiControlFailure.NotFound)]
    [InlineData(HttpStatusCode.Conflict, AiControlFailure.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized, AiControlFailure.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, AiControlFailure.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, AiControlFailure.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, AiControlFailure.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiControlFailure.Unavailable)]
    public async Task UpstreamFailureMapping_DoesNotRelayBody(
        HttpStatusCode status, AiControlFailure expected)
    {
        const string upstreamOnly = "upstream-body-must-not-cross-boundary";
        var client = Client((_, _) => Task.FromResult(Json(status,
            $$"""{"error":"{{upstreamOnly}}"}""")));

        var result = await client.GetSnapshotAsync();

        Assert.Equal(expected, result.Failure);
        Assert.DoesNotContain(upstreamOnly, JsonSerializer.Serialize(result, WebJson),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RequestsValidateClosedProviderAndAuthContracts()
    {
        Assert.Null(new AiPoolCreateRequest
        {
            Provider = "codex-sdk", Label = "Codex", Weight = 1,
        }.Validate());
        Assert.NotNull(new AiPoolCreateRequest
        {
            Provider = "openrouter", Label = "Nope", Weight = 1,
        }.Validate());

        Assert.Null(new AiAccountCreateRequest
        {
            PoolId = "pool-1", Label = "Claude", AuthType = "api-key",
        }.Validate());
        Assert.NotNull(new AiAccountCreateRequest
        {
            PoolId = "pool-1", Label = "Claude", AuthType = "arbitrary",
        }.Validate());
        Assert.NotNull(new AiAccountCreateRequest
        {
            PoolId = "pool-1", Label = "Codex", AuthType = "chatgpt-device",
            Credential = "must-use-device-flow",
        }.Validate());
    }

    [Theory]
    [InlineData(-100, 1, true)]
    [InlineData(100, 100, true)]
    [InlineData(-101, 1, false)]
    [InlineData(101, 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, 101, false)]
    public void PoolRanges_MatchRbAiContractExactly(int priority, int weight, bool valid)
    {
        var error = new AiPoolCreateRequest
        {
            Provider = "codex-sdk",
            Label = "Codex",
            Priority = priority,
            Weight = weight,
        }.Validate();

        Assert.Equal(valid, error is null);
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(32_768, true)]
    [InlineData(32_769, false)]
    public void CredentialLength_MatchesRbAiContractExactly(int length, bool valid)
    {
        var error = new AiCredentialReplaceRequest
        {
            Credential = new string('x', length),
        }.Validate();

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void CredentialLength_UsesSameTrimmedValueAsRbAi()
    {
        Assert.Null(new AiCredentialReplaceRequest
        {
            Credential = $"  {new string('x', 32_768)}  ",
        }.Validate());
        Assert.NotNull(new AiCredentialReplaceRequest { Credential = "  1234567  " }.Validate());
        Assert.Null(new AiCredentialReplaceRequest { Credential = "  12345678  " }.Validate());
    }

    [Fact]
    public void AccountCreate_EnforcesCredentialLengthBoundary()
    {
        var request = new AiAccountCreateRequest
        {
            PoolId = "pool-1",
            Label = "Claude",
            AuthType = "api-key",
            Credential = "1234567",
        };

        Assert.NotNull(request.Validate());

        request = new AiAccountCreateRequest
        {
            PoolId = "pool-1",
            Label = "Claude",
            AuthType = "api-key",
            Credential = "12345678",
        };

        Assert.Null(request.Validate());
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private const string SnapshotJson = """
        {
          "generation": 7,
          "models": [
            {"alias":"codex","provider":"codex-sdk","model":"gpt-5.6-sol","capabilities":["structured-output"]}
          ],
          "providers": [
            {"id":"codex-sdk","configuredAccounts":2,"availableAccounts":1,"inFlight":0,"status":"ready"}
          ],
          "pools": [
            {"id":"pool-1","provider":"codex-sdk","label":"Codex","enabled":true,"priority":10,"weight":1,"source":"managed","editable":true,"accountCount":2,"availableAccounts":1,"status":"ready"}
          ],
          "accounts": [
            {"id":"account-1","poolId":"pool-1","label":"Primary","enabled":true,"authType":"access-token","status":"ready","lastTestedAt":null,"credentialConfigured":true,"editable":true}
          ]
        }
        """;

    private static AiControlClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        ILogger<AiControlClient>? logger = null) =>
        new(
            new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://rb-ai.test") },
            new AiControlOptions(ControlKey),
            logger ?? new CapturingLogger<AiControlClient>());

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, null));
    }
}
