using System.Text.Json;
using System.Text.Json.Serialization;

namespace RbRules.Domain;

/// <summary>Pure loader voor de voorbeeld-gouden-set (#231). Deserialiseert het
/// seed-JSON (<c>RbRules.Tests/Fixtures/poracle-eval-seed.json</c>) naar <see
/// cref="EvalCase"/>-records. Dit is bewust GEEN productie-pad: in de bedrade
/// versie leeft het corpus in Postgres <c>eval_case</c> en genereert rb-ai
/// kandidaat-cases uit set/errata-diffs → reviewqueue. Het JSON-bestand is de
/// menselijk-leesbare seed én de vorm waarin rb-ai later cases zou aanleveren;
/// deze parser bewijst dat die vorm rondloopt.</summary>
public static class EvalSeed
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<EvalCase> Parse(string json)
    {
        var seed = JsonSerializer.Deserialize<SeedDto>(json, Options)
            ?? throw new JsonException("Seed-JSON is null of leeg.");
        return [.. seed.Cases.Select(Map)];
    }

    private static EvalCase Map(CaseDto dto) => new()
    {
        Id = Require(dto.Id, nameof(dto.Id)),
        Question = Require(dto.Question, nameof(dto.Question)),
        QueryType = dto.QueryType,
        Status = dto.Status,
        ValidFrom = dto.ValidFrom ?? DateOnly.MinValue,
        ValidUntil = dto.ValidUntil,
        SupersededByErratum = dto.SupersededByErratum,
        GoldSupport = dto.GoldSupport ?? [],
        ExpectedCitations = dto.ExpectedCitations ?? [],
        ForbiddenClaims =
        [
            .. (dto.ForbiddenClaims ?? []).Select(f => new ForbiddenClaim(
                Require(f.Id, nameof(f.Id)),
                Require(f.Text, nameof(f.Text)),
                f.SupersededByErratum)),
        ],
    };

    private static string Require(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new JsonException($"Seed-case mist verplicht veld '{field}'.")
            : value;

    private sealed record SeedDto(List<CaseDto> Cases);

    private sealed record CaseDto(
        string? Id,
        string? Question,
        EvalQueryType QueryType,
        EvalStatus Status,
        DateOnly? ValidFrom,
        DateOnly? ValidUntil,
        string? SupersededByErratum,
        List<string>? GoldSupport,
        List<string>? ExpectedCitations,
        List<ForbiddenClaimDto>? ForbiddenClaims);

    private sealed record ForbiddenClaimDto(
        string? Id, string? Text, string? SupersededByErratum);
}
