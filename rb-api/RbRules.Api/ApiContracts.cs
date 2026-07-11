namespace RbRules.Api;

public record SourcePatch(
    string? Name, string? Url, short? TrustTier, int? Rank, string? Cadence, bool? Enabled);

/// <summary>Beheerder-bewerking van een kennisdoc (#70).</summary>
public record KnowledgePatch(string? Title, string? Body);

public record AskRequest(
    string Question, List<AskImageDto>? Images = null, List<AskTurnDto>? History = null);

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

/// <summary>Beheerder-bewerking van een account (#42): blokkeren en quota.</summary>
public record UserPatch(bool? Blocked, int? DailyQuota, int? DailyPhotoQuota);
