using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De werkverdeling deterministisch ↔ LLM (#211) plus het groeiende
/// vocabulaire (#52) en het bewijs-snippet (#123). De kaartteksten in deze
/// tests zijn letterlijk overgenomen uit de live kaartenbak (riftcodex,
/// text.plain) — juist de randgevallen ("Equip" zónder haken, "Repeat" als
/// gewoon werkwoord) bepalen waar de grens tussen regex en LLM ligt.</summary>
public class MechanicMinerTests
{
    // Echte kaartteksten; zie de klasse-docstring.
    private const string JaggedCutlass =
        "Equip :rb_rune_body: (:rb_rune_body:: Attach this to a unit you control.)";
    private const string LaurentBladekeeper =
        "Ganking (I can move from battlefield to battlefield.)";
    private const string SpriteFountain =
        "[Temporary] (Kill this at the start of its controller's Beginning Phase, before scoring.)" +
        "When you play this, play a ready 3 :rb_might: Sprite unit token with [Temporary] to your base." +
        "[Deathknell][&gt;] Repeat this gear's play effect. (When this dies, get the effect.)";
    private const string BloodRush =
        "[Action] (Play on your turn or in showdowns.)[Repeat] :rb_energy_1: (You may pay the " +
        "additional cost to repeat this spell's effect.)Give a unit [Assault 2]. (+2 :rb_might:.)";

    // ── Deterministisch: wat de gebracket-vorm zegt (#211) ──────────────

    [Fact]
    public void Analyze_LeestGebracketeMechaniekenZonderLlm()
    {
        var a = MechanicMiner.Analyze(SpriteFountain);
        // Beide gebrackete keywords, elk één keer ([Temporary] staat er twee
        // keer); "[&gt;]" is de icoon-pijl en telt niet mee.
        Assert.Equal(["Temporary", "Deathknell"], a.Bracketed);
    }

    [Fact]
    public void Analyze_MagnitudeBlijftDeFamilie_NooitEenAparteMechaniek()
    {
        // Kritiek (CanonicalEntity.CanonicalLabel): "Assault 2" en "Assault 3"
        // zijn dezelfde mechaniek — de magnitude mag nooit tot een eigen
        // entiteit uiteenvallen.
        var a = MechanicMiner.Analyze("[Assault 2] here, [Assault 3] there, [Assault] everywhere.");
        Assert.Equal(["Assault"], a.Bracketed);
        Assert.DoesNotContain("Assault 2", a.Bracketed);
        Assert.DoesNotContain("Assault 3", a.Bracketed);
    }

    [Fact]
    public void Analyze_NegeertIconenEnPlaceholders()
    {
        var a = MechanicMiner.Analyze("[&gt;] exhaust me. [NO TEXT] [Level 6] [TANK] [Quick-Draw]");
        // "[TANK]" is geen keyword-vorm (KeywordShape eist Hoofdletter+kleine letter).
        Assert.Equal(["Level", "Quick-Draw"], a.Bracketed);
    }

    [Fact]
    public void Analyze_LaatDoorBeheerVerworpenTermenWeg()
    {
        // Een mens zei "dit is geen mechaniek" — dan komt de term ook niet
        // langs de deterministische route alsnog op de kaart.
        var a = MechanicMiner.Analyze(
            "[Ganking] and [Level 6].", ["Ganking", "Level"], rejected: ["level"]);
        Assert.Equal(["Ganking"], a.Bracketed);
        Assert.Empty(a.Candidates);
    }

    [Fact]
    public void Analyze_LeegVoorLegeTekst()
    {
        Assert.Empty(MechanicMiner.Analyze(null).Bracketed);
        Assert.Empty(MechanicMiner.Analyze("  ").Candidates);
    }

    // ── Het LLM-deel: alleen wat de gebracket-vorm níét kan (#211) ───────

    [Fact]
    public void Analyze_BiedtOngebracketeVocabulaireTermenAanAlsKandidaat()
    {
        // Riot drukt op deze twee kaarten het keyword zónder haken af — precies
        // het gat dat de regex niet dicht: hier moet een oordeel over.
        var equip = MechanicMiner.Analyze(JaggedCutlass, MechanicMiner.Vocabulary(["Equip"]));
        Assert.Empty(equip.Bracketed);
        Assert.Equal(["Equip"], equip.Candidates);

        var ganking = MechanicMiner.Analyze(LaurentBladekeeper, MechanicMiner.Vocabulary(["Ganking"]));
        Assert.Empty(ganking.Bracketed);
        Assert.Equal(["Ganking"], ganking.Candidates);
    }

    [Fact]
    public void Analyze_BiedtEenAlGebracketeMechaniekNietOpnieuwAan()
    {
        // Zuinigheid (#249): over "[Repeat]" hoeft geen LLM te oordelen, over
        // "Repeat this gear's play effect" wél — dezelfde term, andere kaart.
        var bloodRush = MechanicMiner.Analyze(BloodRush, MechanicMiner.Vocabulary(["Repeat", "Assault"]));
        Assert.Equal(["Action", "Repeat", "Assault"], bloodRush.Bracketed);
        Assert.Empty(bloodRush.Candidates);

        var spriteFountain = MechanicMiner.Analyze(SpriteFountain, MechanicMiner.Vocabulary(["Repeat"]));
        Assert.Equal(["Temporary", "Deathknell"], spriteFountain.Bracketed);
        Assert.Equal(["Repeat"], spriteFountain.Candidates);
    }

    [Fact]
    public void Analyze_KandidaatMatchtOpHeelWoord_NietOpDeelwoord()
    {
        // "Leveling" is geen voorkoming van "Level"; anders zou elke toevallige
        // woordstam een LLM-oordeel (en dus kosten) uitlokken.
        var a = MechanicMiner.Analyze("Leveling the battlefield.", MechanicMiner.Vocabulary(["Level"]));
        Assert.Empty(a.Candidates);

        var hit = MechanicMiner.Analyze("Level the battlefield.", MechanicMiner.Vocabulary(["Level"]));
        Assert.Equal(["Level"], hit.Candidates);
    }

    [Fact]
    public void Analyze_BiedtGeenTermenBuitenHetVocabulaireAan()
    {
        // Nieuwe termen lopen via de kandidatenqueue langs een mens (#52) —
        // niet via een LLM-oordeel op losse tekst.
        var a = MechanicMiner.Analyze("Ganking is fun.", MechanicMiner.SeedVocabulary);
        Assert.Empty(a.Candidates);
    }

    // ── Deterministische validatie ná het oordeel (#211) ────────────────

    [Fact]
    public void MergeMechanics_NeemtAlleenAangebodenTermenOver()
    {
        // De LLM stelt één aangeboden term voor (goed) en één die nergens werd
        // aangeboden (hallucinatie/overijver) — die laatste valt weg.
        var merged = MechanicMiner.MergeMechanics(
            ["Reaction"], ["Equip", "Deathknell"], ["Equip"]);
        Assert.Equal(["Reaction", "Equip"], merged);
    }

    [Fact]
    public void MergeMechanics_KanGebracketeMechaniekenNietWegnemen()
    {
        // Een leeg of onvolledig LLM-antwoord mag het deterministische feit
        // nooit uitwissen.
        Assert.Equal(["Temporary", "Deathknell"],
            MechanicMiner.MergeMechanics(["Temporary", "Deathknell"], [], ["Repeat"]));
    }

    [Fact]
    public void MergeMechanics_SpellingVanDeKandidatenlijstWint()
    {
        var merged = MechanicMiner.MergeMechanics([], ["ganking", "GANKING"], ["Ganking"]);
        Assert.Equal(["Ganking"], merged); // genormaliseerd én gededupliceerd
    }

    [Fact]
    public void MergeMechanics_DubbeltEenAlGebracketeTermNiet()
    {
        Assert.Equal(["Equip"], MechanicMiner.MergeMechanics(["Equip"], ["equip"], ["Equip"]));
    }

    // ── Prompt + parser ─────────────────────────────────────────────────

    [Fact]
    public void BuildPrompt_ToontTekst_DeterministischeMechaniekenEnKandidaten()
    {
        var card = new Card
        {
            RiftboundId = "sfd-1", Name = "Jagged Cutlass", Type = "Gear",
            TextPlain = JaggedCutlass,
        };
        var prompt = MechanicMiner.BuildPrompt(
            [new(card, MechanicMiner.Analyze(JaggedCutlass, MechanicMiner.Vocabulary(["Equip", "Unique"])))]);

        Assert.Contains("sfd-1", prompt);
        Assert.Contains("Jagged Cutlass", prompt);
        Assert.Contains("Attach this to a unit", prompt);
        Assert.Contains("mechanics: (geen)", prompt);
        Assert.Contains("kandidaten: Equip", prompt);
    }

    [Fact]
    public void SystemPrompt_VraagtAlleenOmHetOordeelBuitenDeBlokhaken() =>
        Assert.Contains("Neem ze NIET opnieuw op", MechanicMiner.SystemPrompt);

    [Fact]
    public void ParseBatch_ExtraheertJsonArrayUitRommeligAntwoord()
    {
        var raw = """
            Hier is de analyse:
            [{"id": "ogn-1", "extraMechanics": ["Equip"], "triggers": ["when I conquer"], "effects": ["buff might"]},
             {"id": "ogn-2", "extraMechanics": [], "triggers": [], "effects": ["draw a card"]}]
            """;
        var result = MechanicMiner.ParseBatch(raw);
        Assert.Equal(2, result.Count);
        Assert.Equal(["Equip"], result[0].ExtraMechanics);
        Assert.Equal(["when I conquer"], result[0].Triggers);
        Assert.Empty(result[1].ExtraMechanics);
        Assert.Equal(["draw a card"], result[1].Effects);
    }

    [Fact]
    public void ParseBatch_AccepteertOokDeOudeMechanicsSleutel()
    {
        // Tolerantie kost niets: alles gaat daarna toch door MergeMechanics.
        var result = MechanicMiner.ParseBatch("""[{"id": "x", "mechanics": ["Ganking"]}]""");
        Assert.Equal(["Ganking"], result[0].ExtraMechanics);
    }

    [Fact]
    public void ParseBatch_SlaatItemsZonderIdOver()
    {
        var result = MechanicMiner.ParseBatch(
            """[{"extraMechanics": ["Tank"]}, {"id": "ok", "extraMechanics": []}]""");
        Assert.Single(result);
        Assert.Equal("ok", result[0].Id);
    }

    [Fact]
    public void ParseBatch_DegradeertNetjes_OpRommelEnOpEenObjectomhulsel()
    {
        // Nooit een crash (#188-les): zonder array een lege lijst — de kaart
        // blijft dan gewoon in de wachtrij staan.
        Assert.Empty(MechanicMiner.ParseBatch("geen json"));
        Assert.Empty(MechanicMiner.ParseBatch("""{"id": "x"}"""));
        Assert.Empty(MechanicMiner.ParseBatch(""));

        // Een objectomhulsel is geen verlies: het knippen op [ ] haalt het
        // array eruit, zodat een model dat zijn antwoord inpakt tóch telt.
        var wrapped = MechanicMiner.ParseBatch("""{"cards": [{"id": "x", "extraMechanics": []}]}""");
        Assert.Equal("x", Assert.Single(wrapped).Id);
    }

    // ── Groeiend vocabulaire (#52) ──────────────────────────────────────

    [Fact]
    public void Vocabulary_MergesAcceptedKeywords_SeedSpellingWins()
    {
        var vocab = MechanicMiner.Vocabulary(["Ganking", "tank", "  ", "Ganking"]);
        Assert.Contains("Ganking", vocab);
        // "tank" bestaat al als seed-term "Tank" — geen duplicaat erbij.
        Assert.Single(vocab, v => v.Equals("Tank", StringComparison.OrdinalIgnoreCase));
        Assert.Single(vocab, v => v == "Ganking");
    }

    [Fact]
    public void ExtractKeywordCandidates_FindsBracketedTermsOutsideVocabulary()
    {
        // Echte tekstvorm uit de Riftcodex-API (text.plain).
        var candidates = MechanicMiner.ExtractKeywordCandidates(
            "[Ganking] (May move when a showdown starts.) [Action] Deal 4.",
            MechanicMiner.SeedVocabulary);
        Assert.Equal(["Ganking"], candidates); // Action zit al in het vocabulaire
    }

    [Fact]
    public void ExtractKeywordCandidates_StripsNumericParameterAndDedupes()
    {
        var candidates = MechanicMiner.ExtractKeywordCandidates(
            "[Assault 2] here, [Assault] there, [Hunt 2] everywhere.",
            MechanicMiner.SeedVocabulary);
        Assert.Equal(["Assault", "Hunt"], candidates);
    }

    [Fact]
    public void ExtractKeywordCandidates_IgnoresIconArrowsAndPlaceholderNoise()
    {
        // "[&gt;]" is de ge-escapete pijl-icoon, "[NO TEXT]" een placeholder;
        // vocab-termen tellen case-insensitive niet mee als kandidaat.
        var candidates = MechanicMiner.ExtractKeywordCandidates(
            "[&gt;] exhaust me. [NO TEXT] [Level 6] [TANK] [Quick-Draw]",
            MechanicMiner.SeedVocabulary);
        Assert.Equal(["Level", "Quick-Draw"], candidates);
    }

    [Fact]
    public void ExtractKeywordCandidates_EmptyForEmptyText()
    {
        Assert.Empty(MechanicMiner.ExtractKeywordCandidates(null, MechanicMiner.SeedVocabulary));
        Assert.Empty(MechanicMiner.ExtractKeywordCandidates("  ", MechanicMiner.SeedVocabulary));
    }

    // ── Bewijs bij kandidaten (#123) ────────────────────────────────────

    [Fact]
    public void SnippetFor_SplitsAroundBracketedTerm()
    {
        var s = MechanicMiner.SnippetFor("Play a unit. [Ganking] (May move.)", "Ganking");
        Assert.NotNull(s);
        Assert.Equal("Play a unit. ", s.Before);
        Assert.Equal("[Ganking]", s.Match);
        Assert.Equal(" (May move.)", s.After);
    }

    [Fact]
    public void SnippetFor_MatchesParameterizedForm_LikeTheMiner()
    {
        // "[Assault 2]" hoort bij kandidaat "Assault" (numerieke parameter
        // gestript, zie Analyze) — de match toont de volledige bracketed vorm.
        var s = MechanicMiner.SnippetFor("[Assault 2] Deal 4.", "Assault");
        Assert.NotNull(s);
        Assert.Equal("[Assault 2]", s.Match);
        Assert.Equal("", s.Before);
    }

    [Fact]
    public void SnippetFor_IsCaseInsensitive_LikeCandidateDedupe()
    {
        var s = MechanicMiner.SnippetFor("[Quick-Draw] shoot first.", "quick-draw");
        Assert.NotNull(s);
        Assert.Equal("[Quick-Draw]", s.Match);
    }

    [Fact]
    public void SnippetFor_TruncatesLongContextWithEllipsis()
    {
        var text = new string('a', 100) + " [Level 6] " + new string('b', 100);
        var s = MechanicMiner.SnippetFor(text, "Level", context: 20);
        Assert.NotNull(s);
        Assert.StartsWith("…", s.Before);
        Assert.EndsWith("…", s.After);
        // "…" + 20 tekens context; de match zelf blijft volledig.
        Assert.Equal(21, s.Before.Length);
        Assert.Equal(21, s.After.Length);
        Assert.Equal("[Level 6]", s.Match);
    }

    [Fact]
    public void SnippetFor_NullWhenTermAbsentOrNotBracketed()
    {
        // "Leveling" mag geen match voor "Level" zijn, en een kale (niet-
        // bracketed) voorkoming telt niet — het snippet toont alleen [..].
        Assert.Null(MechanicMiner.SnippetFor("[Leveling] and Level up.", "Level"));
        Assert.Null(MechanicMiner.SnippetFor(null, "Level"));
        Assert.Null(MechanicMiner.SnippetFor("[Level 6]", "  "));
    }
}
