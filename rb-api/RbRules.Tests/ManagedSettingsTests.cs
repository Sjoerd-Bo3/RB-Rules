using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Api;
using RbRules.Domain;
using RbRules.Infrastructure;
using RbRules.Infrastructure.GraphRag;

namespace RbRules.Tests;

/// <summary>Beheerde instellingen (#254): feature-vlaggen uit de beheerpagina in
/// plaats van de VM-<c>.env</c>. Bewijst de drie eisen die deze laag moet dragen:
/// (1) ZONDER DB-waarde verandert er niets aan het bestaande env-/codegedrag,
/// (2) een toggle heeft DIRECT effect — dezelfde service-instantie, geen herstart —
/// en (3) elke wijziging laat een auditregel achter (rode draad #236: geen
/// onzichtbare state). Plus de validatie die voorkomt dat beheer een schakelaar
/// omzet die stilletjes niets doet.</summary>
public class ManagedSettingsTests
{
    // ── 1) Regressie: geen DB-waarde ⇒ exact het bestaande gedrag ─────────

    [Fact]
    public void WithOverrides_Leeg_LaatDeEnvWaardenOngemoeid()
    {
        var brein = new BreinRetrievalSettings(Enabled: true, TokenBudget: 1234);
        var nightly = new NightlyRunSettings(3, 9, "Europe/Amsterdam", Enabled: false);
        var leeg = new Dictionary<string, string>();

        // Record-gelijkheid: élk veld ongewijzigd, niet alleen de vlag.
        Assert.Equal(brein, brein.WithOverrides(leeg));
        Assert.Equal(nightly, nightly.WithOverrides(leeg));
    }

    [Fact]
    public async Task LegeSettingTabel_GeeftPreciesDeEnvBootstrapWaarden()
    {
        // De harde eis uit #254: tot iemand bewust schakelt is de beheerde laag
        // onzichtbaar. Een lege tabel mag geen enkel veld verschuiven.
        var brein = new BreinRetrievalSettings(Enabled: true, TokenBudget: 999, GdsWarm: true);
        var nightly = new NightlyRunSettings(1, 8, "Europe/Berlin", Enabled: false);
        var svc = Service(NewFactory(), brein, nightly);

        Assert.Equal(brein, await svc.BreinRetrievalAsync());
        Assert.Equal(nightly, await svc.NightlyAsync());
        Assert.Empty(await svc.OverridesAsync());
    }

    [Fact]
    public async Task LegeSettingTabel_BreinRetrievalBlijftDefaultUit()
    {
        // De veiligheidsdefault van #228 blijft de veiligheidsdefault.
        var svc = Service(NewFactory(), BreinRetrievalSettings.Disabled, NightlyRunSettings.Default);
        Assert.False((await svc.BreinRetrievalAsync()).Enabled);
    }

    // ── 2) Een toggle werkt DIRECT, zonder herstart ───────────────────────

    [Fact]
    public async Task Toggle_WerktDirect_OpDezelfdeServiceInstantie()
    {
        var factory = NewFactory();
        var svc = Service(factory, BreinRetrievalSettings.Disabled, NightlyRunSettings.Default);
        Assert.False((await svc.BreinRetrievalAsync()).Enabled); // vult meteen de cache

        var change = await svc.SetAsync(SettingKeys.BreinRetrievalEnabled, "aan", "beheer");

        Assert.True(change.Ok);
        Assert.Equal("false", change.Previous);
        Assert.Equal("true", change.Current);
        // GEEN nieuwe service, geen herstart: de eerstvolgende lezing ziet het al.
        Assert.True((await svc.BreinRetrievalAsync()).Enabled);
    }

    [Fact]
    public async Task Nachtrun_Noodrem_EnVenster_ZijnBeheerbaar_MetDirectEffect()
    {
        var svc = Service(NewFactory(), BreinRetrievalSettings.Disabled,
            new NightlyRunSettings(0, 11, "Europe/Amsterdam"));

        Assert.True((await svc.NightlyAsync()).Enabled);
        Assert.True((await svc.SetAsync(SettingKeys.NightlyEnabled, "uit", "beheer")).Ok);
        Assert.False((await svc.NightlyAsync()).Enabled);

        Assert.True((await svc.SetAsync(SettingKeys.NightlyStartHour, "1", "beheer")).Ok);
        Assert.True((await svc.SetAsync(SettingKeys.NightlyEndHour, "6", "beheer")).Ok);
        Assert.True((await svc.SetAsync(SettingKeys.NightlyTimeZone, "Europe/Berlin", "beheer")).Ok);

        var nightly = await svc.NightlyAsync();
        Assert.Equal(1, nightly.StartHour);
        Assert.Equal(6, nightly.EndHour);
        Assert.Equal("Europe/Berlin", nightly.TimeZoneId);
        Assert.False(nightly.Enabled);
    }

    [Fact]
    public async Task Terugzetten_ZonderWaarde_HerstelDeEnvDefault()
    {
        var factory = NewFactory();
        var svc = Service(factory, BreinRetrievalSettings.Disabled, NightlyRunSettings.Default);
        await svc.SetAsync(SettingKeys.BreinRetrievalEnabled, "true", "beheer");
        Assert.True((await svc.BreinRetrievalAsync()).Enabled);

        var change = await svc.SetAsync(SettingKeys.BreinRetrievalEnabled, null, "beheer");

        Assert.True(change.Ok);
        Assert.False((await svc.BreinRetrievalAsync()).Enabled);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.Settings.ToListAsync()); // de rij is weg, niet "false"
    }

    // ── 3) Elke wijziging laat een spoor na ───────────────────────────────

    [Fact]
    public async Task Wijziging_LandtAlsAuditregelInRunLog_MetOudeEnNieuweWaarde()
    {
        var factory = NewFactory();
        var svc = Service(factory, BreinRetrievalSettings.Disabled, NightlyRunSettings.Default);

        await svc.SetAsync(SettingKeys.BreinRetrievalEnabled, "true", "sjoerd");

        await using var db = await factory.CreateDbContextAsync();
        var log = Assert.Single(await db.RunLogs.Where(r => r.Kind == "setting").ToListAsync());
        Assert.Equal(SettingKeys.BreinRetrievalEnabled, log.Ref);
        Assert.Equal("changed", log.Status);
        Assert.Contains("false → true", log.Detail);
        Assert.Contains("sjoerd", log.Detail);

        var row = Assert.Single(await db.Settings.ToListAsync());
        Assert.Equal("sjoerd", row.UpdatedBy);
    }

    [Fact]
    public async Task ZelfdeWaardeOpnieuwZetten_LaatGeenLegeAuditregelAchter()
    {
        var factory = NewFactory();
        var svc = Service(factory, BreinRetrievalSettings.Disabled, NightlyRunSettings.Default);
        await svc.SetAsync(SettingKeys.BreinRetrievalEnabled, "true", "beheer");

        var again = await svc.SetAsync(SettingKeys.BreinRetrievalEnabled, "true", "beheer");

        Assert.True(again.Ok);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.RunLogs.Where(r => r.Kind == "setting").ToListAsync());
    }

    // ── 4) Validatie: nooit een schakelaar die stilletjes niets doet ──────

    [Theory]
    [InlineData("onbekend.sleutel", "true")]
    [InlineData(SettingKeys.BreinRetrievalEnabled, "misschien")]
    [InlineData(SettingKeys.NightlyStartHour, "25")]
    [InlineData(SettingKeys.NightlyStartHour, "ochtend")]
    [InlineData(SettingKeys.NightlyTimeZone, "Mars/Olympus_Mons")]
    public async Task OngeldigeWaarde_WordtGeweigerdMetUitleg_EnSchrijftNiets(string key, string value)
    {
        var factory = NewFactory();
        var svc = Service(factory, BreinRetrievalSettings.Disabled, NightlyRunSettings.Default);

        var change = await svc.SetAsync(key, value, "beheer");

        Assert.False(change.Ok);
        Assert.False(string.IsNullOrWhiteSpace(change.Error));
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.Settings.ToListAsync());
        Assert.Empty(await db.RunLogs.ToListAsync());
    }

    [Fact]
    public async Task VensterOverMiddernacht_WordtGeweigerd_MetUitleg()
    {
        // start >= eind breekt de eenmaal-per-dag-dedup (NightlyWindow.RanToday).
        // Env valt daar stil op de default terug; beheer weigert het zichtbaar.
        var svc = Service(NewFactory(), BreinRetrievalSettings.Disabled,
            new NightlyRunSettings(0, 11, "Europe/Amsterdam"));

        var change = await svc.SetAsync(SettingKeys.NightlyStartHour, "22", "beheer");

        Assert.False(change.Ok);
        Assert.Contains("kalenderdag", change.Error);
        Assert.Equal(0, (await svc.NightlyAsync()).StartHour);
    }

    [Fact]
    public async Task Venster_VerschuivenInEenKeer_Lukt_OokAlsDeTussenstapOngeldigZou_Zijn()
    {
        // 0–11 → 12–18: los toegepast strandt de eerste stap (12 >= 11). Samen
        // aangeboden telt alleen de eindtoestand — daarom is de schrijfpoort een set.
        var svc = Service(NewFactory(), BreinRetrievalSettings.Disabled,
            new NightlyRunSettings(0, 11, "Europe/Amsterdam"));

        var result = await svc.SetManyAsync(
        [
            new(SettingKeys.NightlyStartHour, "12"),
            new(SettingKeys.NightlyEndHour, "18"),
        ], "beheer");

        Assert.True(result.Ok);
        var nightly = await svc.NightlyAsync();
        Assert.Equal(12, nightly.StartHour);
        Assert.Equal(18, nightly.EndHour);
    }

    [Fact]
    public async Task SamengesteldeWijziging_MetEenFout_SchrijftNiets()
    {
        // Alles-of-niets: een half doorgevoerd venster is erger dan geen wijziging.
        var factory = NewFactory();
        var svc = Service(factory, BreinRetrievalSettings.Disabled,
            new NightlyRunSettings(0, 11, "Europe/Amsterdam"));

        var result = await svc.SetManyAsync(
        [
            new(SettingKeys.NightlyStartHour, "2"),
            new(SettingKeys.NightlyEndHour, "onzin"),
        ], "beheer");

        Assert.False(result.Ok);
        Assert.Equal(0, (await svc.NightlyAsync()).StartHour);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.Settings.ToListAsync());
    }

    [Fact]
    public void ParseValue_NormaliseertNaarDeOpslagvorm()
    {
        // Genormaliseerd opslaan zorgt dat de twee bestaande env-parsers (die nét
        // andere woorden accepteren) het over een opgeslagen waarde altijd eens zijn.
        Assert.Equal("true",
            ManagedSettingsCatalog.ParseValue(SettingKeys.NightlyEnabled, "AAN").Value);
        Assert.Equal("false",
            ManagedSettingsCatalog.ParseValue(SettingKeys.NightlyEnabled, "Uit").Value);
        Assert.Equal("7",
            ManagedSettingsCatalog.ParseValue(SettingKeys.NightlyStartHour, " 7 ").Value);
    }

    [Fact]
    public void GenormaliseerdeWaarden_WordenDoorBeideEnvParsersGelijkGelezen()
    {
        var aan = new Dictionary<string, string>
        {
            [SettingKeys.BreinRetrievalEnabled] = "true",
            [SettingKeys.NightlyEnabled] = "true",
        };
        var uit = new Dictionary<string, string>
        {
            [SettingKeys.BreinRetrievalEnabled] = "false",
            [SettingKeys.NightlyEnabled] = "false",
        };

        Assert.True(BreinRetrievalSettings.Disabled.WithOverrides(aan).Enabled);
        Assert.False(new BreinRetrievalSettings(Enabled: true).WithOverrides(uit).Enabled);
        Assert.True(NightlyRunSettings.Default with { Enabled = false } is var n
            && n.WithOverrides(aan).Enabled);
        Assert.False(NightlyRunSettings.Default.WithOverrides(uit).Enabled);
    }

    [Fact]
    public void WithOverrides_OnleesbaarVenster_ValtTerugOpDeBasis()
    {
        // Handmatig in de DB gerommeld (het endpoint weigert dit): het venster valt
        // terug op de basis i.p.v. de nachtrun helemaal onmogelijk te maken. De
        // aan/uit-vlag staat daar bewust los van.
        var basis = new NightlyRunSettings(0, 11, "Europe/Amsterdam");
        var kapot = new Dictionary<string, string>
        {
            [SettingKeys.NightlyStartHour] = "22",
            [SettingKeys.NightlyEndHour] = "6",
            [SettingKeys.NightlyEnabled] = "false",
        };

        var result = basis.WithOverrides(kapot);

        Assert.Equal(0, result.StartHour);
        Assert.Equal(11, result.EndHour);
        Assert.False(result.Enabled);
    }

    // ── 5) Cache: goedkoop op het hete pad, maar begrensd ─────────────────

    [Fact]
    public async Task BuitenOmGewijzigd_WerktDoorNaDeTtl_NietEerder()
    {
        // Eigen schrijfacties invalideren de cache meteen (zie hierboven). Deze test
        // dekt het andere geval: iemand zet de rij rechtstreeks in de DB. Dan geldt
        // de TTL — daarom is die kort.
        var factory = NewFactory();
        var clock = new TestClock(DateTimeOffset.Parse("2026-07-20T12:00:00Z"));
        var svc = Service(factory, BreinRetrievalSettings.Disabled, NightlyRunSettings.Default, clock);
        Assert.False((await svc.BreinRetrievalAsync()).Enabled);

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Settings.Add(new Setting
            {
                Key = SettingKeys.BreinRetrievalEnabled, Value = "true", UpdatedBy = "sql",
            });
            await db.SaveChangesAsync();
        }

        // Binnen de TTL: nog de gecachete waarde (geen query per /ask-vraag).
        Assert.False((await svc.BreinRetrievalAsync()).Enabled);

        clock.Now += ManagedSettingsService.Ttl + TimeSpan.FromSeconds(1);
        Assert.True((await svc.BreinRetrievalAsync()).Enabled);
    }

    [Fact]
    public async Task DatabaseWeg_ValtTerugOpDeBootstrapWaarden_ZonderExceptie()
    {
        // Fouten zijn data: een db-hik mag /ask of de scheduler niet stilleggen.
        var nightly = new NightlyRunSettings(2, 9, "Europe/Amsterdam");
        var svc = new ManagedSettingsService(
            new ThrowingFactory(), breinBase: BreinRetrievalSettings.Disabled, nightlyBase: nightly);

        Assert.Equal(nightly, await svc.NightlyAsync());
        Assert.False((await svc.BreinRetrievalAsync()).Enabled);
    }

    // ── 6) Wat beheer te zien krijgt ──────────────────────────────────────

    [Fact]
    public async Task ListAsync_ToontEffectiefNaastDeEnvDefault_EnWieHetZette()
    {
        var svc = Service(NewFactory(), BreinRetrievalSettings.Disabled, NightlyRunSettings.Default);
        await svc.SetAsync(SettingKeys.BreinRetrievalEnabled, "true", "sjoerd");

        var views = await svc.ListAsync();

        Assert.Equal(ManagedSettingsCatalog.All.Count, views.Count);
        var retrieval = views.Single(v => v.Key == SettingKeys.BreinRetrievalEnabled);
        Assert.Equal("true", retrieval.Effective);
        Assert.Equal("false", retrieval.Default); // de env-waarde blijft zichtbaar
        Assert.True(retrieval.Overridden);
        Assert.Equal("sjoerd", retrieval.UpdatedBy);
        Assert.NotNull(retrieval.UpdatedAt);

        // Niet-geschakelde instellingen tonen zichzelf als "niet overschreven".
        var tz = views.Single(v => v.Key == SettingKeys.NightlyTimeZone);
        Assert.False(tz.Overridden);
        Assert.Equal(tz.Default, tz.Effective);
    }

    // ── 7) Het koppelvlak met rb-web ──────────────────────────────────────

    [Fact]
    public void SettingsPatch_LeestDeBodyDieRbWebStuurt()
    {
        // rb-web's `setting`-action postet {changes:[{key,value}],actor}. Deze test
        // is de vangrail tegen een stille 400/415 in productie: de vorm is met een
        // stub geverifieerd, maar de échte binding gebruikt de Web-defaults
        // (camelCase, hoofdletterongevoelig) van ASP.NET Core.
        var json = """
            {"changes":[{"key":"nightly.start_hour","value":"12"},
                        {"key":"nightly.end_hour","value":"18"}],"actor":"beheer"}
            """;

        var patch = JsonSerializer.Deserialize<SettingsPatch>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(patch);
        Assert.Equal("beheer", patch.Actor);
        Assert.Equal(2, patch.Changes!.Count);
        Assert.Equal(SettingKeys.NightlyStartHour, patch.Changes[0].Key);
        Assert.Equal("12", patch.Changes[0].Value);
        Assert.Equal(SettingKeys.NightlyEndHour, patch.Changes[1].Key);
        Assert.Equal("18", patch.Changes[1].Value);
    }

    [Fact]
    public void SettingsPatch_LegeWaarde_BetekentTerugNaarDeStandaard()
    {
        var patch = JsonSerializer.Deserialize<SettingsPatch>(
            """{"changes":[{"key":"brein.retrieval.enabled","value":""}]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("", Assert.Single(patch!.Changes!).Value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ManagedSettingsService Service(
        IDbContextFactory<RbRulesDbContext> factory,
        BreinRetrievalSettings brein, NightlyRunSettings nightly, TimeProvider? clock = null) =>
        new(factory, breinBase: brein, nightlyBase: nightly, clock: clock);

    private static IDbContextFactory<RbRulesDbContext> NewFactory() =>
        new InMemoryFactory(Guid.NewGuid().ToString());

    private sealed class InMemoryFactory(string name) : IDbContextFactory<RbRulesDbContext>
    {
        public RbRulesDbContext CreateDbContext() => new InMemoryDbContext(
            new DbContextOptionsBuilder<RbRulesDbContext>().UseInMemoryDatabase(name).Options);
    }

    private sealed class ThrowingFactory : IDbContextFactory<RbRulesDbContext>
    {
        public RbRulesDbContext CreateDbContext() =>
            throw new InvalidOperationException("postgres weg");
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

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
