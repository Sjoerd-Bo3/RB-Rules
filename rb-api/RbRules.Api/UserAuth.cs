using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api;

/// <summary>Poort op de LLM-routes (#42). Anoniem mag door — de per-IP-rate-
/// limit is dan de rem; een geldige X-User-Token geeft de ruimere per-account-
/// dagquota. Alleen vragen (AskRequest) tellen tegen het dagquotum; de overige
/// LLM-routes (resolve/explain/feedback) kennen hier de blokkade-check en
/// blijven verder op de rate-limiter leunen. Bewust een endpoint-filter en
/// geen AskService-verbouwing: het streaming-werk (#31) raakt dezelfde flow.</summary>
public class UserQuotaFilter : IEndpointFilter
{
    public const string TokenHeader = "X-User-Token";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Ask-geschiedenis (#157): vóór de token-check, want ook anonieme
        // requests krijgen een ip_hash op RequestUserContext — zelfde
        // client-IP-patroon als de rate-limiter (Program.cs). Ontbreekt
        // ASK_IP_HASH_SECRET of het IP: IpHashing.Hash geeft null (stille
        // degradatie, geen ip_hash op de trace).
        var clientIp = http.Request.Headers["X-Client-Ip"].FirstOrDefault()
            ?? http.Connection.RemoteIpAddress?.ToString();
        http.RequestServices.GetRequiredService<RequestUserContext>().IpHash =
            IpHashing.Hash(clientIp, Environment.GetEnvironmentVariable("ASK_IP_HASH_SECRET"));

        var token = http.Request.Headers[TokenHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(token)) return await next(context);

        var accounts = http.RequestServices.GetRequiredService<UserAccountService>();
        var user = await accounts.ResolveSessionAsync(token);
        if (user is null)
            return Results.Json(new { error = "je sessie is verlopen — log opnieuw in" },
                statusCode: StatusCodes.Status401Unauthorized);

        // Endpoint-filters draaien ná model-binding: het AskRequest-argument
        // vertelt of dit een vraag is (en of er een foto bij zit).
        var ask = context.Arguments.OfType<AskRequest>().FirstOrDefault();
        var usage = ask is null
            ? new UsageToday(0, 0)
            : await accounts.UsageTodayAsync(user.Id);
        var check = Accounts.CheckQuota(
            user.Blocked, user.DailyQuota, user.DailyPhotoQuota,
            usage.Questions, usage.Photos,
            countsQuestion: ask is not null,
            hasImage: ask?.Images?.Any(i => !string.IsNullOrWhiteSpace(i.Data)) == true);
        if (!check.Allowed)
            return Results.Json(new { error = check.Message },
                statusCode: check.Verdict == QuotaVerdict.Blocked
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status429TooManyRequests);

        // Voor de duur van dit request bekend — AskService stempelt hiermee
        // ask_metric/ask_trace op het account. Het al getelde verbruik gaat
        // mee (#153): de aanpak-beslissing toetst daarop het Grondig-quotum
        // zonder een tweede telling.
        var requestUser = http.RequestServices.GetRequiredService<RequestUserContext>();
        requestUser.User = user;
        requestUser.Usage = ask is null ? null : usage;
        return await next(context);
    }
}
