using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De extractie-instellingen van #323: model-alias (gesloten map) en
/// batch-K, plus het batch-callbudget dat met K meeschaalt. Alle grenzen en
/// model-ID's staan hier als UITGESCHREVEN literals — een assertie tegen de
/// constante die ze bewaakt schuift mee (#286/#293-les), dus een bewuste
/// wijziging hoort deze tests rood te maken.</summary>
public class BreinExtractSettingsTests
{
    // ── De gesloten aliasmap (verificatiepunt 5, rb-api-kant) ────────────────

    [Fact]
    public void ModelId_VertaaltDeAliassenNaarLetterlijkeModelIds()
    {
        Assert.Equal("claude-fable-5", BreinExtractModels.ModelId("fable"));
        Assert.Equal("claude-opus-4-8", BreinExtractModels.ModelId("opus"));
        Assert.Equal("claude-sonnet-4-6", BreinExtractModels.ModelId("sonnet"));
        // 1M-contextvarianten (Agent SDK-notatie model[1m]).
        Assert.Equal("claude-fable-5[1m]", BreinExtractModels.ModelId("fable-1m"));
        Assert.Equal("claude-sonnet-4-6[1m]", BreinExtractModels.ModelId("sonnet-1m"));
    }

    [Fact]
    public void ModelId_WeigertAllesBuitenDeMap()
    {
        Assert.Null(BreinExtractModels.ModelId("gpt-5"));
        // Een RAUW model-ID is geen alias: de map is de enige toegang.
        Assert.Null(BreinExtractModels.ModelId("claude-fable-5"));
        Assert.Null(BreinExtractModels.ModelId(null));
        Assert.Null(BreinExtractModels.ModelId(""));
        Assert.False(BreinExtractModels.IsAlias("fable "));
    }

    [Fact]
    public void Aliases_ZijnExactDeVijfAfgesproken()
    {
        Assert.Equal(["sonnet", "opus", "fable", "fable-1m", "sonnet-1m"],
            BreinExtractModels.Aliases);
        Assert.Equal("fable", BreinExtractModels.DefaultAlias);
    }

    // ── Defaults en env-bootstrap ────────────────────────────────────────────

    [Fact]
    public void Default_IsFableMetK50()
    {
        // Expliciete productkeuze van Sjoerd: fable, 50 kaarten per sessie.
        Assert.Equal("fable", BreinExtractSettings.Default.ModelAlias);
        Assert.Equal(50, BreinExtractSettings.Default.BatchK);
        Assert.Equal(90_000, BreinExtractSettings.Default.BaseTimeoutMs);
        Assert.Equal(180_000, BreinExtractSettings.Default.PerCardTimeoutMs);
    }

    [Fact]
    public void MaxBatchK_IsLetterlijk250()
    {
        Assert.Equal(250, BreinExtractSettings.MaxBatchK);
    }

    // ── Beheerde overrides (#254-patroon) — clamp met literals (punt 6) ─────

    [Fact]
    public void WithOverrides_GeldigeWaarden_Winnen()
    {
        var s = BreinExtractSettings.Default.WithOverrides(new Dictionary<string, string>
        {
            ["brein.extract.model"] = "opus",
            ["brein.extract.batch_k"] = "17",
        });
        Assert.Equal("opus", s.ModelAlias);
        Assert.Equal(17, s.BatchK);
    }

    [Theory]
    [InlineData("0")]     // onder de ondergrens 1
    [InlineData("251")]   // boven de bovengrens 250
    [InlineData("-3")]
    [InlineData("junk")]
    public void WithOverrides_BatchK_BuitenBereik_ValtTerugOpDeBasis(string raw)
    {
        var s = BreinExtractSettings.Default.WithOverrides(
            new Dictionary<string, string> { ["brein.extract.batch_k"] = raw });
        Assert.Equal(50, s.BatchK);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("250")]
    public void WithOverrides_BatchK_GrenzenZelf_ZijnLegaal(string raw)
    {
        var s = BreinExtractSettings.Default.WithOverrides(
            new Dictionary<string, string> { ["brein.extract.batch_k"] = raw });
        Assert.Equal(int.Parse(raw), s.BatchK);
    }

    [Fact]
    public void WithOverrides_OnbekendeAlias_ValtTerugOpDeBasis()
    {
        // Vangnet tegen handmatige SQL — de catalogus-poort hoort dit al te
        // weigeren; hier mag het nooit een vrije string richting rb-ai worden.
        var s = BreinExtractSettings.Default.WithOverrides(
            new Dictionary<string, string> { ["brein.extract.model"] = "gpt-5" });
        Assert.Equal("fable", s.ModelAlias);
    }

    // ── Het batch-callbudget schaalt mee met K (verificatiepunt 4) ──────────

    [Fact]
    public void BatchCallTimeout_SchaaltMeeMetK_Letterlijk()
    {
        // Mutatie-anker: zet de schaling uit (alleen basis + marge) en deze
        // uitgeschreven waarden gaan rood. 90000 + (K−1)×180000 + 120000 marge.
        var s = BreinExtractSettings.Default;
        Assert.Equal(TimeSpan.FromMilliseconds(1_470_000), s.BatchCallTimeout(8));
        Assert.Equal(TimeSpan.FromMilliseconds(210_000), s.BatchCallTimeout(1));
        Assert.Equal(TimeSpan.FromMilliseconds(390_000), s.BatchCallTimeout(2));
    }

    [Fact]
    public void BatchCallTimeout_IsRuimerDanDeRbAiKetenEronder()
    {
        // De #281-les als invariant: het rb-api-budget moet boven rb-ai's eigen
        // keten (basis + (K−1)×per-kaart) liggen, anders verkleedt elke
        // upstream-fout zich als "traag".
        var s = BreinExtractSettings.Default;
        for (var k = 1; k <= BreinExtractSettings.MaxBatchK; k += 49)
        {
            var chain = TimeSpan.FromMilliseconds(
                s.BaseTimeoutMs + (long)(k - 1) * s.PerCardTimeoutMs);
            Assert.True(s.BatchCallTimeout(k) > chain,
                $"budget voor K={k} moet boven de rb-ai-keten liggen");
        }
    }

    [Fact]
    public void BatchCallTimeout_KlemtKOpHetLegaleBereik()
    {
        var s = BreinExtractSettings.Default;
        Assert.Equal(s.BatchCallTimeout(1), s.BatchCallTimeout(0));
        Assert.Equal(s.BatchCallTimeout(250), s.BatchCallTimeout(999));
    }

    // ── De catalogus-poort van de twee nieuwe sleutels (punt 5/6) ────────────

    [Fact]
    public void Catalogus_KentDeTweeNieuweSleutels()
    {
        Assert.NotNull(ManagedSettingsCatalog.Find("brein.extract.model"));
        Assert.NotNull(ManagedSettingsCatalog.Find("brein.extract.batch_k"));
    }

    [Fact]
    public void Catalogus_Model_AccepteertAliassenEnWeigertDeRest()
    {
        var ok = ManagedSettingsCatalog.ParseValue("brein.extract.model", "fable");
        Assert.True(ok.Ok);
        Assert.Equal("fable", ok.Value);

        // Case-insensitieve invoer normaliseert naar de catalogus-vorm.
        var upper = ManagedSettingsCatalog.ParseValue("brein.extract.model", "FABLE-1M");
        Assert.True(upper.Ok);
        Assert.Equal("fable-1m", upper.Value);

        Assert.False(ManagedSettingsCatalog.ParseValue("brein.extract.model", "gpt-5").Ok);
        // Een raw model-ID is geen keuze — de alias-map is de enige toegang.
        Assert.False(ManagedSettingsCatalog.ParseValue(
            "brein.extract.model", "claude-fable-5").Ok);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("250", true)]
    [InlineData("0", false)]
    [InlineData("251", false)]
    public void Catalogus_BatchK_HanteertDezelfde1Tot250AlsDeClamp(string raw, bool ok)
    {
        // De catalogus-poort en de afnemer-clamp moeten dezelfde grenzen
        // hanteren — een waarde die hier door mag maar daarna stil geclampt
        // wordt is een schakelaar die liegt.
        Assert.Equal(ok, ManagedSettingsCatalog.ParseValue("brein.extract.batch_k", raw).Ok);
    }

    [Fact]
    public void AuditSampleN_HoudtHetOude1Tot100Bereik()
    {
        // De Min/Max-uitbreiding van SettingDefinition mag de bestaande
        // Count-sleutel niet stil verruimen.
        Assert.True(ManagedSettingsCatalog.ParseValue("brein.audit.sample_n", "100").Ok);
        Assert.False(ManagedSettingsCatalog.ParseValue("brein.audit.sample_n", "101").Ok);
    }

    // ── ManagedSettingsService-doorvoer (#254: DB wint, gebruiksmoment) ─────

    [Fact]
    public async Task BreinExtractAsync_SeedOverride_WintVanDeEnvBasis()
    {
        var svc = new ManagedSettingsService(
            extractBase: new BreinExtractSettings("fable", 50, 90_000, 180_000),
            seed: new Dictionary<string, string>
            {
                ["brein.extract.model"] = "sonnet",
                ["brein.extract.batch_k"] = "3",
            });
        var s = await svc.BreinExtractAsync();
        Assert.Equal("sonnet", s.ModelAlias);
        Assert.Equal(3, s.BatchK);
    }

    [Fact]
    public async Task BreinExtractAsync_ZonderOverride_GeeftDeBasis()
    {
        var svc = new ManagedSettingsService(
            extractBase: new BreinExtractSettings("fable", 50, 90_000, 180_000));
        var s = await svc.BreinExtractAsync();
        Assert.Equal("fable", s.ModelAlias);
        Assert.Equal(50, s.BatchK);
    }
}
