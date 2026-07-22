using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RbRules.Infrastructure;

/// <summary>Startup configuration for the private rb-ai control plane. The key is
/// deliberately kept outside managed settings and never rendered by ToString.</summary>
public sealed class AiControlOptions
{
    public AiControlOptions(string? controlKey) =>
        ControlKey = string.IsNullOrWhiteSpace(controlKey) ? null : controlKey;

    public string? ControlKey { get; }

    public static AiControlOptions FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("RB_AI_CONTROL_KEY"));

    public override string ToString() =>
        $"{nameof(AiControlOptions)} {{ ControlKey = [redacted] }}";
}

public enum AiControlFailure
{
    None,
    InvalidRequest,
    NotFound,
    Conflict,
    Unavailable,
}

/// <summary>Transport-neutral result. It intentionally carries no upstream error
/// body or exception text: those are uncontrolled channels that may reflect a
/// credential.</summary>
public sealed class AiControlResult<T>
{
    private AiControlResult(T? value, AiControlFailure failure)
    {
        Value = value;
        Failure = failure;
    }

    public bool Ok => Failure == AiControlFailure.None;
    public T? Value { get; }
    public AiControlFailure Failure { get; }

    public static AiControlResult<T> Success(T value) => new(value, AiControlFailure.None);
    public static AiControlResult<T> Failed(AiControlFailure failure) => new(default, failure);

    public AiControlResult<TOut> Map<TOut>(Func<T, TOut> map) =>
        Ok && Value is not null
            ? AiControlResult<TOut>.Success(map(Value))
            : AiControlResult<TOut>.Failed(Failure == AiControlFailure.None
                ? AiControlFailure.Unavailable
                : Failure);
}

// Read-side DTOs are an allow-list. Unknown JSON fields (including a mistakenly
// echoed credential/access token) are discarded during deserialization and can
// therefore never cross the rb-api response boundary.
public sealed record AiControlSnapshot(
    long Generation,
    IReadOnlyList<AiModelView> Models,
    IReadOnlyList<AiProviderView> Providers,
    IReadOnlyList<AiPoolView> Pools,
    IReadOnlyList<AiAccountView> Accounts);

public sealed record AiModelView(
    string Alias, string Provider, string Model, IReadOnlyList<string> Capabilities);

public sealed record AiProviderView(
    string Id, int ConfiguredAccounts, int AvailableAccounts,
    int InFlight, string Status, bool Configured = false);

public sealed record AiPoolView(
    string Id, string Provider, string Label, bool Enabled, int Priority,
    int Weight, string Source, bool Editable, int AccountCount,
    int AvailableAccounts, string Status);

public sealed record AiAccountView(
    string Id, string PoolId, string Label, bool Enabled, string AuthType,
    string Status, bool CredentialConfigured, bool Editable,
    DateTimeOffset? LastTestedAt = null);

public sealed record AiAccountTestView(
    string AccountId, string Status, DateTimeOffset? LastTestedAt);

public sealed record AiDeviceLoginStartView(
    string SessionId, string VerificationUri, string UserCode,
    DateTimeOffset? ExpiresAt, int IntervalSeconds);

/// <remarks>Detail is intentionally not relayed. It is an uncontrolled upstream
/// string and adds no state beyond the closed Status value.</remarks>
public sealed record AiDeviceLoginStatusView(
    string Status, int? PollAfterMs, string? Detail = null);

public sealed class AiPoolCreateRequest
{
    public string? Provider { get; init; }
    public string? Label { get; init; }
    public bool? Enabled { get; init; }
    public int? Priority { get; init; }
    public int? Weight { get; init; }

    public string? Validate() => AiControlValidation.Pool(
        Provider, Label, Priority, Weight, requireProvider: true, requireLabel: true);
}

public sealed class AiPoolPatchRequest
{
    public string? Label { get; init; }
    public bool? Enabled { get; init; }
    public int? Priority { get; init; }
    public int? Weight { get; init; }

    public string? Validate()
    {
        if (Label is null && Enabled is null && Priority is null && Weight is null)
            return "Geef minstens één poolwijziging op.";
        return AiControlValidation.Pool(
            provider: null, Label, Priority, Weight, requireProvider: false, requireLabel: false);
    }
}

/// <summary>Account creation may carry a one-time credential. This class is not a
/// record and its string representation is fixed, so structured logging cannot
/// expand the secret by accident.</summary>
public sealed class AiAccountCreateRequest
{
    public string? PoolId { get; init; }
    public string? Label { get; init; }
    public string? AuthType { get; init; }
    public bool? Enabled { get; init; }
    public string? Credential { get; init; }

    public string? Validate()
    {
        if (!AiControlValidation.ValidId(PoolId)) return "Ongeldige pool-id.";
        if (!AiControlValidation.ValidLabel(Label)) return "Accountlabel is verplicht (maximaal 80 tekens).";
        if (!AiControlContract.AuthTypes.Contains(AuthType, StringComparer.Ordinal))
            return "Onbekend authenticatietype.";
        if (AuthType == AiControlContract.ChatGptDevice && Credential is not null)
            return "Een ChatGPT-device-login accepteert geen credential.";
        return AiControlValidation.OptionalCredential(Credential);
    }

    public override string ToString() =>
        $"{nameof(AiAccountCreateRequest)} {{ PoolId = {PoolId}, Label = {Label}, "
        + $"AuthType = {AuthType}, Enabled = {Enabled}, Credential = [redacted] }}";
}

public sealed class AiAccountPatchRequest
{
    public string? Label { get; init; }
    public bool? Enabled { get; init; }

    public string? Validate()
    {
        if (Label is null && Enabled is null) return "Geef minstens één accountwijziging op.";
        return Label is not null && !AiControlValidation.ValidLabel(Label)
            ? "Accountlabel is verplicht (maximaal 80 tekens)."
            : null;
    }
}

public sealed class AiCredentialReplaceRequest
{
    public string? Credential { get; init; }

    public string? Validate() => AiControlValidation.RequiredCredential(Credential);

    public override string ToString() =>
        $"{nameof(AiCredentialReplaceRequest)} {{ Credential = [redacted] }}";
}

public static class AiControlContract
{
    public const string ClaudeProvider = "claude-agent-sdk";
    public const string CodexProvider = "codex-sdk";
    public const string ClaudeOAuth = "oauth-token";
    public const string ClaudeApiKey = "api-key";
    public const string CodexAccessToken = "access-token";
    public const string ChatGptDevice = "chatgpt-device";

    public static readonly IReadOnlyList<string> Providers =
        [ClaudeProvider, CodexProvider];
    public static readonly IReadOnlyList<string> AuthTypes =
        [ClaudeOAuth, ClaudeApiKey, CodexAccessToken, ChatGptDevice];
}

internal static class AiControlValidation
{
    // Exact rb-ai vault contract (control/types.ts).
    private const int MinCredentialLength = 8;
    private const int MaxCredentialLength = 32_768;

    public static string? Pool(
        string? provider, string? label, int? priority, int? weight,
        bool requireProvider, bool requireLabel)
    {
        if (requireProvider && !AiControlContract.Providers.Contains(provider, StringComparer.Ordinal))
            return "Onbekende AI-provider.";
        if (provider is not null
            && !AiControlContract.Providers.Contains(provider, StringComparer.Ordinal))
            return "Onbekende AI-provider.";
        if (requireLabel && !ValidLabel(label))
            return "Poollabel is verplicht (maximaal 80 tekens).";
        if (label is not null && !ValidLabel(label))
            return "Poollabel is verplicht (maximaal 80 tekens).";
        if (priority is < -100 or > 100) return "Prioriteit moet tussen -100 en 100 liggen.";
        if (weight is <= 0 or > 100) return "Gewicht moet tussen 1 en 100 liggen.";
        return null;
    }

    public static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static bool ValidLabel(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 80
        && !value.Any(char.IsControl);

    public static string? RequiredCredential(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Credential is verplicht.";

        // rb-ai trims before applying its 8..32768 vault boundary. Validate the
        // same normalized value here so the public facade accepts exactly the
        // credentials that the private control plane can persist and redact.
        var length = value.Trim().Length;
        if (length < MinCredentialLength)
            return "Credential moet minimaal 8 tekens bevatten.";
        return length > MaxCredentialLength ? "Credential is te groot." : null;
    }

    public static string? OptionalCredential(string? value) =>
        value is null ? null : RequiredCredential(value);
}

internal sealed record AiPoolEnvelope(AiPoolView Pool);
internal sealed record AiAccountEnvelope(AiAccountView Account);
internal sealed record AiDeviceLoginStatusWire(
    string Status, int? PollAfterMs = null, string? Detail = null);

/// <summary>Dedicated typed client for rb-ai's private control plane. It never
/// writes settings/audit rows, never logs a body, and never returns an upstream
/// error string.</summary>
public sealed class AiControlClient(
    HttpClient http, AiControlOptions options, ILogger<AiControlClient> logger)
{
    public const string ControlHeaderName = "X-RB-AI-Control-Key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<AiControlResult<AiControlSnapshot>> GetSnapshotAsync(
        CancellationToken ct = default) =>
        (await SendAsync<AiControlSnapshot>(HttpMethod.Get, "/control", body: null, ct)
            .ConfigureAwait(false)).Map(snapshot => snapshot with
        {
            Models = snapshot.Models ?? [],
            Pools = snapshot.Pools ?? [],
            Accounts = snapshot.Accounts ?? [],
            // `configured` is derived rather than trusted as an independent flag;
            // this also keeps the facade correct across an older rb-ai snapshot
            // that supplied only the aggregate count.
            Providers = (snapshot.Providers ?? [])
                .Select(provider => provider with
                {
                    Configured = provider.ConfiguredAccounts > 0,
                })
                .ToList(),
        });

    public async Task<AiControlResult<AiPoolView>> CreatePoolAsync(
        AiPoolCreateRequest request, CancellationToken ct = default) =>
        (await SendAsync<AiPoolEnvelope>(HttpMethod.Post, "/control/pools", request, ct)
            .ConfigureAwait(false)).Map(x => x.Pool);

    public async Task<AiControlResult<AiPoolView>> PatchPoolAsync(
        string id, AiPoolPatchRequest request, CancellationToken ct = default) =>
        (await SendAsync<AiPoolEnvelope>(HttpMethod.Patch,
            $"/control/pools/{Segment(id)}", request, ct).ConfigureAwait(false)).Map(x => x.Pool);

    public Task<AiControlResult<bool>> DeletePoolAsync(
        string id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"/control/pools/{Segment(id)}", ct);

    public async Task<AiControlResult<AiAccountView>> CreateAccountAsync(
        AiAccountCreateRequest request, CancellationToken ct = default) =>
        (await SendAsync<AiAccountEnvelope>(HttpMethod.Post, "/control/accounts", request, ct)
            .ConfigureAwait(false)).Map(x => x.Account);

    public async Task<AiControlResult<AiAccountView>> PatchAccountAsync(
        string id, AiAccountPatchRequest request, CancellationToken ct = default) =>
        (await SendAsync<AiAccountEnvelope>(HttpMethod.Patch,
            $"/control/accounts/{Segment(id)}", request, ct).ConfigureAwait(false)).Map(x => x.Account);

    public Task<AiControlResult<bool>> DeleteAccountAsync(
        string id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"/control/accounts/{Segment(id)}", ct);

    public async Task<AiControlResult<AiAccountView>> ReplaceCredentialAsync(
        string id, AiCredentialReplaceRequest request, CancellationToken ct = default) =>
        (await SendAsync<AiAccountEnvelope>(HttpMethod.Put,
            $"/control/accounts/{Segment(id)}/credential", request, ct).ConfigureAwait(false))
        .Map(x => x.Account);

    public Task<AiControlResult<AiAccountTestView>> TestAccountAsync(
        string id, CancellationToken ct = default) =>
        SendAsync<AiAccountTestView>(HttpMethod.Post,
            $"/control/accounts/{Segment(id)}/test", body: null, ct);

    public Task<AiControlResult<AiDeviceLoginStartView>> StartDeviceLoginAsync(
        string id, CancellationToken ct = default) =>
        SendAsync<AiDeviceLoginStartView>(HttpMethod.Post,
            $"/control/accounts/{Segment(id)}/device-login", body: null, ct);

    public async Task<AiControlResult<AiDeviceLoginStatusView>> GetDeviceLoginAsync(
        string sessionId, CancellationToken ct = default) =>
        (await SendAsync<AiDeviceLoginStatusWire>(HttpMethod.Get,
            $"/control/device-login/{Segment(sessionId)}", body: null, ct).ConfigureAwait(false))
        // Upstream detail is deliberately dropped; Status is a closed enum and is
        // sufficient for polling without creating a free-text leak channel.
        .Map(x => new AiDeviceLoginStatusView(x.Status, x.PollAfterMs));

    public Task<AiControlResult<bool>> CancelDeviceLoginAsync(
        string sessionId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete,
            $"/control/device-login/{Segment(sessionId)}", ct);

    private async Task<AiControlResult<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (options.ControlKey is null)
            return AiControlResult<T>.Failed(AiControlFailure.Unavailable);

        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ControlHeaderName, options.ControlKey);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);

        try
        {
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = MapFailure(response.StatusCode);
                logger.LogWarning(
                    "rb-ai-control weigerde {Method} met status {StatusCode}; antwoordbody niet gelezen",
                    method.Method, (int)response.StatusCode);
                return AiControlResult<T>.Failed(failure);
            }

            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
                .ConfigureAwait(false);
            return value is null
                ? AiControlResult<T>.Failed(AiControlFailure.Unavailable)
                : AiControlResult<T>.Success(value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("rb-ai-control {Method} verliep op de interne timeout", method.Method);
            return AiControlResult<T>.Failed(AiControlFailure.Unavailable);
        }
        catch (HttpRequestException ex)
        {
            // Only the exception TYPE is logged. Message/cause may contain an
            // upstream reflection of the write-only request.
            logger.LogWarning("rb-ai-control {Method} niet bereikbaar ({ExceptionType})",
                method.Method, ex.GetType().Name);
            return AiControlResult<T>.Failed(AiControlFailure.Unavailable);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("rb-ai-control {Method} gaf ongeldige JSON ({ExceptionType})",
                method.Method, ex.GetType().Name);
            return AiControlResult<T>.Failed(AiControlFailure.Unavailable);
        }
    }

    private async Task<AiControlResult<bool>> SendNoContentAsync(
        HttpMethod method, string path, CancellationToken ct)
    {
        if (options.ControlKey is null)
            return AiControlResult<bool>.Failed(AiControlFailure.Unavailable);

        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ControlHeaderName, options.ControlKey);
        try
        {
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return AiControlResult<bool>.Success(true);
            logger.LogWarning(
                "rb-ai-control weigerde {Method} met status {StatusCode}; antwoordbody niet gelezen",
                method.Method, (int)response.StatusCode);
            return AiControlResult<bool>.Failed(MapFailure(response.StatusCode));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("rb-ai-control {Method} verliep op de interne timeout", method.Method);
            return AiControlResult<bool>.Failed(AiControlFailure.Unavailable);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("rb-ai-control {Method} niet bereikbaar ({ExceptionType})",
                method.Method, ex.GetType().Name);
            return AiControlResult<bool>.Failed(AiControlFailure.Unavailable);
        }
    }

    private static AiControlFailure MapFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
            AiControlFailure.InvalidRequest,
        HttpStatusCode.NotFound => AiControlFailure.NotFound,
        HttpStatusCode.Conflict => AiControlFailure.Conflict,
        _ => AiControlFailure.Unavailable,
    };

    private static string Segment(string value) => Uri.EscapeDataString(value);
}
