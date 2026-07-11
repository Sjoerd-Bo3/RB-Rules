namespace RbRules.Api;

public record SourcePatch(
    string? Name, string? Url, short? TrustTier, int? Rank, string? Cadence, bool? Enabled);

public record AskRequest(
    string Question, List<AskImageDto>? Images = null, List<AskTurnDto>? History = null);

/// <summary>Eerdere ronde in een doorvraag-gesprek (#41).</summary>
public record AskTurnDto(string Question, string Answer);

public record AskImageDto(string MediaType, string Data);

public record ResolveRequest(string[] CardIds);

public record CorrectionSubmit(string Question, string Verdict, string? Text);

public record PushSubscribe(string Endpoint, string P256dh, string Auth);

public record PushUnsubscribe(string Endpoint);
