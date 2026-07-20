using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RbRules.Domain;
using RbRules.Infrastructure.GraphRag;

namespace RbRules.Infrastructure;

/// <summary>Eén beheerbare instelling zoals beheer hem ziet (#254): wat hij nu doet
/// (<paramref name="Effective"/>), wat hij zónder DB-rij zou doen
/// (<paramref name="Default"/>, de env-/codewaarde) en of er bewust overheen is
/// geschreven.</summary>
public sealed record ManagedSettingView(
    string Key, string Kind, string Group, string Label, string Description,
    string Effective, string Default, bool Overridden,
    DateTimeOffset? UpdatedAt, string? UpdatedBy);

/// <summary>Eén te zetten instelling. <paramref name="Value"/> null/leeg = terug naar
/// de env-/codewaarde.</summary>
public sealed record SettingAssignment(string Key, string? Value);

/// <summary>Uitkomst van één schakelaar-omzetting. <paramref name="Error"/> null =
/// gelukt; anders een uitlegbare weigering (onbekende sleutel, onzin-waarde, ongeldig
/// venster) die beheer letterlijk kan tonen.</summary>
public sealed record SettingChange(
    string Key, string? Previous, string? Current, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>Uitkomst van een (mogelijk samengestelde) wijziging. Alles-of-niets:
/// <paramref name="Error"/> gezet ⇒ er is NIETS geschreven.</summary>
public sealed record SettingsChangeResult(
    IReadOnlyList<SettingChange> Changes, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>De beheerde instellingen-laag (#254): leest feature-vlaggen op het
/// GEBRUIKSMOMENT uit de <c>setting</c>-tabel in plaats van één keer bij startup uit
/// de omgeving, zodat een toggle in beheer direct effect heeft — zonder SSH,
/// <c>.env</c>-aanpassing of herstart.
///
/// KERN-INVARIANT: de omgeving blijft de BOOTSTRAP-DEFAULT. Geen rij in de tabel ⇒
/// de bestaande env-/codewaarde geldt onveranderd. Een lege tabel (de toestand vlak
/// na deze migratie) gedraagt zich dus exact als vóór #254.
///
/// KOSTEN: singleton met een in-memory snapshot van de hele (piepkleine) tabel.
/// Schrijven gaat door deze service en invalideert de cache meteen, dus binnen dit
/// proces is een toggle onmiddellijk zichtbaar. De TTL is alleen een vangnet voor
/// waarden die BUITEN de service in de DB belanden (handmatige SQL, een tweede
/// instantie): dan duurt het hooguit <see cref="Ttl"/>. Op het hete pad (/ask) kost
/// een lezing daardoor in de regel nul I/O — alleen na een TTL-verval één
/// <c>SELECT</c> over enkele rijen.
///
/// DB-uitval is een verwacht pad: de laatst bekende snapshot blijft gelden (koude
/// start: de env-defaults), met een korte back-off zodat een hik geen storm van
/// mislukte query's oplevert. Een vlag bevriezen is hier het juiste gedrag —
/// /ask en de scheduler mogen niet omvallen omdat Postgres even hikt.</summary>
public sealed class ManagedSettingsService
{
    /// <summary>Hoe lang een snapshot geldig is zonder eigen schrijfactie. Kort genoeg
    /// dat een buiten-om-wijziging binnen een halve minuut doorwerkt, lang genoeg dat
    /// een reeks /ask-verzoeken niet elk een query kost.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly IDbContextFactory<RbRulesDbContext>? _dbFactory;
    private readonly ILogger<ManagedSettingsService>? _logger;
    private readonly BreinRetrievalSettings _breinBase;
    private readonly NightlyRunSettings _nightlyBase;
    private readonly BreinAuditSettings _auditBase;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private readonly TimeProvider _clock;

    private IReadOnlyDictionary<string, string> _snapshot = Empty;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <param name="dbFactory">Null (unit-tests, patroon <c>AskService.dbFactory</c>) ⇒
    /// geen DB-laag: alleen de basiswaarden plus wat via <paramref name="seed"/> is
    /// meegegeven.</param>
    /// <param name="seed">Vooraf gezette overrides voor tests.</param>
    public ManagedSettingsService(
        IDbContextFactory<RbRulesDbContext>? dbFactory = null,
        ILogger<ManagedSettingsService>? logger = null,
        BreinRetrievalSettings? breinBase = null,
        NightlyRunSettings? nightlyBase = null,
        IReadOnlyDictionary<string, string>? seed = null,
        TimeProvider? clock = null,
        BreinAuditSettings? auditBase = null)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _breinBase = breinBase ?? BreinRetrievalSettings.FromEnvironment();
        _nightlyBase = nightlyBase ?? NightlyRunSettings.FromEnvironment();
        _auditBase = auditBase ?? BreinAuditSettings.FromEnvironment();
        _clock = clock ?? TimeProvider.System;
        // Zonder dbFactory is de seed de enige waarheid (de leespoort keert dan
        // meteen terug); mét dbFactory is hij hooguit de startwaarde tot de eerste
        // echte lezing.
        if (seed is not null) _snapshot = seed;
    }

    /// <summary>De env-/codewaarden zoals ze zonder enige DB-rij gelden — wat beheer
    /// toont als "default".</summary>
    public BreinRetrievalSettings BreinRetrievalDefault => _breinBase;
    public NightlyRunSettings NightlyDefault => _nightlyBase;

    /// <summary>De brein-retrieval-instellingen op DIT moment (env + overrides).</summary>
    public async Task<BreinRetrievalSettings> BreinRetrievalAsync(CancellationToken ct = default) =>
        _breinBase.WithOverrides(await OverridesAsync(ct).ConfigureAwait(false));

    /// <summary>De nachtrun-instellingen op DIT moment (env + overrides).</summary>
    public async Task<NightlyRunSettings> NightlyAsync(CancellationToken ct = default) =>
        _nightlyBase.WithOverrides(await OverridesAsync(ct).ConfigureAwait(false));

    /// <summary>De audit-instellingen (#255) op DIT moment (env + overrides).</summary>
    public async Task<BreinAuditSettings> BreinAuditAsync(CancellationToken ct = default) =>
        _auditBase.WithOverrides(await OverridesAsync(ct).ConfigureAwait(false));

    /// <summary>De beheerde overrides zoals ze nu gelden. Leeg = alles op de
    /// env-/codewaarde.</summary>
    public async Task<IReadOnlyDictionary<string, string>> OverridesAsync(
        CancellationToken ct = default)
    {
        if (_dbFactory is null) return _snapshot;
        if (_clock.GetUtcNow() - _loadedAt < Ttl) return _snapshot;

        await _reload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Dubbele controle: een parallelle aanroeper kan net herladen hebben.
            if (_clock.GetUtcNow() - _loadedAt < Ttl) return _snapshot;
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.Settings.AsNoTracking()
                .Select(s => new { s.Key, s.Value })
                .ToListAsync(ct).ConfigureAwait(false);
            _snapshot = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
            _loadedAt = _clock.GetUtcNow();
            return _snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fouten zijn data (§CONVENTIES): loggen, de laatst bekende waarden
            // aanhouden en pas na de TTL opnieuw proberen. Een db-hik mag /ask of de
            // scheduler nooit stilleggen.
            _logger?.LogWarning(ex,
                "Beheerde instellingen niet te lezen — laatst bekende waarden blijven gelden");
            _loadedAt = _clock.GetUtcNow();
            return _snapshot;
        }
        finally
        {
            _reload.Release();
        }
    }

    /// <summary>Alles wat beheer moet tonen: per catalogus-sleutel de effectieve
    /// waarde, de env-default en wie/wanneer er overheen schreef.</summary>
    public async Task<IReadOnlyList<ManagedSettingView>> ListAsync(CancellationToken ct = default)
    {
        var overrides = await OverridesAsync(ct).ConfigureAwait(false);
        var meta = await MetaAsync(ct).ConfigureAwait(false);
        var brein = _breinBase.WithOverrides(overrides);
        var nightly = _nightlyBase.WithOverrides(overrides);
        var audit = _auditBase.WithOverrides(overrides);

        return ManagedSettingsCatalog.All.Select(d => new ManagedSettingView(
            Key: d.Key,
            Kind: d.Kind.ToString().ToLowerInvariant(),
            Group: d.Group,
            Label: d.Label,
            Description: d.Description,
            Effective: Render(d.Key, brein, nightly, audit),
            Default: Render(d.Key, _breinBase, _nightlyBase, _auditBase),
            Overridden: overrides.ContainsKey(d.Key),
            UpdatedAt: meta.GetValueOrDefault(d.Key)?.UpdatedAt,
            UpdatedBy: meta.GetValueOrDefault(d.Key)?.UpdatedBy)).ToList();
    }

    /// <summary>Zet één instelling — dunne schil om <see cref="SetManyAsync"/>.
    /// <paramref name="rawValue"/> null/leeg = TERUG naar de env-/codewaarde.</summary>
    public async Task<SettingChange> SetAsync(
        string key, string? rawValue, string actor, CancellationToken ct = default)
    {
        var result = await SetManyAsync([new(key, rawValue)], actor, ct).ConfigureAwait(false);
        return result.Changes.FirstOrDefault()
            ?? new SettingChange(key, null, null, result.Error);
    }

    /// <summary>Zet één of meer instellingen ALS GEHEEL. Samen, want het nachtvenster
    /// is een paar: los toegepast zou "0–11 wordt 12–18" op de eerste stap stranden
    /// (12 &gt;= 11) terwijl de eindtoestand prima klopt. Alles-of-niets: faalt één
    /// waarde de validatie, dan wordt er niets geschreven.
    ///
    /// Elke geslaagde wijziging laat een auditregel in <c>run_log</c> achter
    /// (Kind="setting", met oude → nieuwe waarde en wie het deed) — een schakelaar mag
    /// geen onzichtbare state opleveren (rode draad #236). De cache wordt daarna
    /// meteen ongeldig gemaakt, dus de eerstvolgende lezing (ook midden in een lopend
    /// proces) ziet de nieuwe waarde: dát is het "direct effect zonder herstart".</summary>
    public async Task<SettingsChangeResult> SetManyAsync(
        IReadOnlyList<SettingAssignment> assignments, string actor,
        CancellationToken ct = default)
    {
        if (assignments.Count == 0)
            return new SettingsChangeResult([], "Geen instelling opgegeven.");
        if (_dbFactory is null)
            return new SettingsChangeResult([],
                "Instellingen zijn niet beschikbaar zonder database.");

        // 1. Valideren/normaliseren — vóór elke schrijfactie.
        var parsed = new List<(SettingDefinition Def, string? Value)>();
        foreach (var a in assignments)
        {
            if (ManagedSettingsCatalog.Find(a.Key) is not { } def)
                return new SettingsChangeResult([], $"Onbekende instelling '{a.Key}'.");
            if (string.IsNullOrWhiteSpace(a.Value))
            {
                parsed.Add((def, null)); // terug naar de env-/codewaarde
                continue;
            }
            var p = ManagedSettingsCatalog.ParseValue(a.Key, a.Value);
            if (!p.Ok) return new SettingsChangeResult([], p.Error);
            parsed.Add((def, p.Value));
        }

        var before = await OverridesAsync(ct).ConfigureAwait(false);
        var beforeBrein = _breinBase.WithOverrides(before);
        var beforeNightly = _nightlyBase.WithOverrides(before);
        var beforeAudit = _auditBase.WithOverrides(before);

        var candidate = new Dictionary<string, string>(before, StringComparer.Ordinal);
        foreach (var (def, value) in parsed)
        {
            if (value is null) candidate.Remove(def.Key); else candidate[def.Key] = value;
        }

        // 2. Het venster als GEHEEL toetsen (start < eind), op de BEDOELDE uren: na het
        // terugval-vangnet van WithOverrides ziet een ongeldige combinatie er geldig
        // uit en zou de knop stilletjes niets doen. Alleen wanneer er een uur in het
        // spel is — de noodrem moet altijd te bedienen blijven, ook als er ooit een
        // scheef venster in de DB staat.
        if (parsed.Any(p => p.Def.Kind == SettingKind.Hour))
        {
            var (start, end) = _nightlyBase.IntendedWindow(candidate);
            if (ManagedSettingsCatalog.ValidateWindow(start, end) is { } windowError)
                return new SettingsChangeResult([], windowError);
        }

        var afterBrein = _breinBase.WithOverrides(candidate);
        var afterNightly = _nightlyBase.WithOverrides(candidate);
        var afterAudit = _auditBase.WithOverrides(candidate);

        // 3. Alleen echte wijzigingen schrijven (geen audit-ruis bij een dubbelklik).
        var changes = new List<SettingChange>();
        var writes = new List<(SettingDefinition Def, string? Value, string Previous, string Current)>();
        foreach (var (def, value) in parsed)
        {
            var previous = Render(def.Key, beforeBrein, beforeNightly, beforeAudit);
            var current = Render(def.Key, afterBrein, afterNightly, afterAudit);
            changes.Add(new SettingChange(def.Key, previous, current, null));
            if (current != previous || before.ContainsKey(def.Key) != candidate.ContainsKey(def.Key))
                writes.Add((def, value, previous, current));
        }
        if (writes.Count == 0) return new SettingsChangeResult(changes, null);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            foreach (var (def, value, previous, current) in writes)
            {
                var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == def.Key, ct)
                    .ConfigureAwait(false);
                if (value is null)
                {
                    if (row is not null) db.Settings.Remove(row);
                }
                else if (row is null)
                {
                    db.Settings.Add(new Setting
                    {
                        Key = def.Key, Value = value,
                        UpdatedAt = _clock.GetUtcNow(), UpdatedBy = actor,
                    });
                }
                else
                {
                    row.Value = value;
                    row.UpdatedAt = _clock.GetUtcNow();
                    row.UpdatedBy = actor;
                }

                db.RunLogs.Add(new RunLog
                {
                    Kind = "setting",
                    Ref = def.Key,
                    Status = "changed",
                    Detail = $"{def.Label}: {previous} → {current}"
                             + (value is null ? " (terug naar de standaard)" : "")
                             + $" · door {actor}",
                    CreatedAt = _clock.GetUtcNow(),
                });
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Instellingen niet opgeslagen");
            return new SettingsChangeResult([], $"Opslaan mislukt: {ex.Message}");
        }

        Invalidate();
        return new SettingsChangeResult(changes, null);
    }

    /// <summary>Maak de cache ongeldig zodat de eerstvolgende lezing de DB raadpleegt.
    /// Publiek zodat een toekomstig extern schrijfpad hem ook kan aanroepen.</summary>
    public void Invalidate()
    {
        if (_dbFactory is not null) _loadedAt = DateTimeOffset.MinValue;
    }

    private async Task<Dictionary<string, Setting>> MetaAsync(CancellationToken ct)
    {
        if (_dbFactory is null) return new Dictionary<string, Setting>(StringComparer.Ordinal);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.Settings.AsNoTracking()
                .ToDictionaryAsync(s => s.Key, s => s, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Alleen de "wanneer/door wie"-kolommen ontbreken dan; de waarden zelf
            // komen uit de snapshot en blijven kloppen.
            _logger?.LogWarning(ex, "Instellingen-metadata niet te lezen");
            return new Dictionary<string, Setting>(StringComparer.Ordinal);
        }
    }

    /// <summary>De weergavevorm van één sleutel uit een samengestelde instellingen-set —
    /// dezelfde functie voor "effectief" en "default", zodat die twee per definitie
    /// vergelijkbaar zijn.</summary>
    private static string Render(
        string key, BreinRetrievalSettings brein, NightlyRunSettings nightly,
        BreinAuditSettings audit) => key switch
    {
        SettingKeys.BreinRetrievalEnabled => brein.Enabled ? "true" : "false",
        SettingKeys.BreinAuditSampleN => audit.SampleDivisor.ToString(),
        SettingKeys.NightlyEnabled => nightly.Enabled ? "true" : "false",
        SettingKeys.NightlyStartHour => nightly.StartHour.ToString(),
        SettingKeys.NightlyEndHour => nightly.EndHour.ToString(),
        SettingKeys.NightlyTimeZone => nightly.TimeZoneId,
        _ => "",
    };
}
