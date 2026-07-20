using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De Nederlandse weergavelaag over de canonieke Engelse primer
/// (#266). Twee dingen moeten hier hard vastliggen: speltermen en
/// §-verwijzingen komen ONVERTAALD door de vertaalstap (werkafspraak 1 — ze
/// staan op dezelfde pagina als §-citaten en kaartnamen), en een
/// (her)generatie vervangt de Engelse tekst en de Nederlandse weergave altijd
/// samen, terug naar draft — anders ontstaan er twee waarheden.</summary>
public class PrimerTranslationTests
{
    private const string English =
        """
        Runes are the resource system (§201.1). You Channel a Rune at the start
        of your turn and Recycle it later. During a showdown a Unit deals damage
        equal to its Might (§402.3), and Bonus Damage from an Assault trigger is
        added on top. Battlefields are conquered by holding them.
        """;

    [Fact]
    public void Leaks_GoedeVertaling_HoudtSpeltermenEnParagrafen()
    {
        // De vertaling die we WILLEN: Nederlands lopend proza, speltermen en
        // §-verwijzingen ongemoeid.
        const string dutch =
            """
            Runes vormen het resourcesysteem (§201.1). Je Channelt aan het begin
            van je beurt een Rune en Recyclet die later. Tijdens een showdown
            deelt een Unit schade gelijk aan zijn Might (§402.3), en Bonus Damage
            van een Assault-trigger komt daar bovenop. Battlefields verover je
            door ze vast te houden.
            """;

        Assert.Empty(PrimerTranslation.Leaks(English, dutch));
    }

    [Fact]
    public void Leaks_VernederlandsteSpeltermen_WordenGevangen()
    {
        // Precies het lek uit #266: een vertaalstap die "Battlefields" tot
        // "slagvelden" maakt en "Might" tot "kracht".
        const string dutch =
            """
            Runen vormen het resourcesysteem (§201.1). Je kanaliseert aan het
            begin van je beurt een runensteen en hergebruikt die later. Tijdens
            een krachtmeting deelt een eenheid schade gelijk aan zijn kracht
            (§402.3), en bonusschade van een stormloop-trigger komt daar
            bovenop. Slagvelden verover je door ze vast te houden.
            """;

        var leaks = PrimerTranslation.Leaks(English, dutch);

        Assert.Contains("Battlefield", leaks);
        Assert.Contains("Might", leaks);
        Assert.Contains("showdown", leaks);
        Assert.Contains("Unit", leaks);
        Assert.Contains("Bonus Damage", leaks);
        Assert.Contains("Assault", leaks);
    }

    [Fact]
    public void Leaks_SpeltermInAndereVerbuiging_TeltMee()
    {
        // Woordbegin-match: "Battlefields" dekt "Battlefield", en de
        // Nederlandse kant is hoofdletter-ongevoelig ("je unit wordt ready").
        const string dutch =
            "Runes (§201.1). Je Channelt een rune, Recyclet die, en in een "
            + "showdown deelt je unit schade gelijk aan zijn Might (§402.3); "
            + "Bonus Damage van Assault komt erbij. Elk Battlefield verover je "
            + "door het vast te houden.";

        Assert.Empty(PrimerTranslation.Leaks(English, dutch));
    }

    [Fact]
    public void Leaks_ModaalMight_EistGeenSpelterm()
    {
        // Valkuil: "might" als hulpwerkwoord is geen spelterm. Zou de controle
        // hoofdletter-ongevoelig kijken, dan werd elke correcte vertaling
        // hiervan afgekeurd en bleef de pagina onnodig Engels.
        const string english = "You might draw a card during your turn (§101.1).";
        const string dutch = "Je mag tijdens je beurt een kaart trekken (§101.1).";

        Assert.Empty(PrimerTranslation.Leaks(english, dutch));
    }

    [Fact]
    public void Leaks_WeggevallenParagraafverwijzing_WordtGevangen()
    {
        const string dutch =
            "Runes vormen het resourcesysteem (§201.1). Je Channelt een Rune en "
            + "Recyclet die. In een showdown deelt een Unit schade gelijk aan "
            + "zijn Might, en Bonus Damage van Assault komt erbij. Battlefields "
            + "verover je door ze vast te houden.";

        Assert.Contains("402.3", PrimerTranslation.Leaks(English, dutch));
    }

    [Fact]
    public void Leaks_LegeVertaling_IsGeenVertaling()
    {
        Assert.NotEmpty(PrimerTranslation.Leaks(English, "   "));
    }

    [Fact]
    public void SystemPrompt_SchrijftHetGlossariumUit()
    {
        // Prompt en waarborg delen één lijst: een nieuwe spelterm landt
        // automatisch in beide, zodat ze niet uit elkaar kunnen lopen.
        foreach (var term in PrimerTranslation.Glossary)
            Assert.Contains(term, PrimerTranslation.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Dutch", PrimerTranslation.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("§", PrimerTranslation.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Glossary_DektDeTermenUitDeWerkafspraak()
    {
        foreach (var term in new[]
                 {
                     "Rune", "Battlefield", "showdown", "Might", "Bonus Damage",
                     "Equip", "Assault", "Reaction", "Recycle",
                 })
            Assert.Contains(term, PrimerTranslation.Glossary);
    }

    [Fact]
    public void DutchTitle_GeeftDeNederlandseConceptnaam_MetSpeltermenIntact()
    {
        Assert.Equal("De beurtstructuur",
            PrimerTopics.DutchTitle("turn-structure", "The turn structure"));
        Assert.Equal("Battlefields veroveren en punten scoren",
            PrimerTopics.DutchTitle("battlefields-scoring",
                "Conquering battlefields and scoring points"));
    }

    [Fact]
    public void DutchTitle_NullBijBewerkteOfOnbekendeTitel()
    {
        // Een handmatig aangepaste titel mag niet stil door een lijstwaarde
        // uit de code worden overruled; onbekende topics vallen ook terug.
        Assert.Null(PrimerTopics.DutchTitle("turn-structure", "Beurten, herzien door de beheerder"));
        Assert.Null(PrimerTopics.DutchTitle("iets-nieuws", "Something new"));
    }

    [Fact]
    public void AlleTopics_HebbenEenNederlandseTitel()
    {
        foreach (var topic in PrimerTopics.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.TitleNl));
            Assert.NotEqual(topic.Title, topic.TitleNl);
        }
    }

    [Fact]
    public void Apply_VervangtBeideTeksten_EnZetTerugNaarDraft()
    {
        // Twee waarheden voorkomen: her-generatie schrijft de Engelse body en
        // de Nederlandse weergave in één beweging en vraagt opnieuw review —
        // de beheerder keurt dus altijd het paar goed dat de bezoeker ziet.
        var doc = new KnowledgeDoc
        {
            Kind = "primer", Topic = "combat", Title = "Combat and showdowns",
            Body = "Oude Engelse tekst.", BodyNl = "Oude Nederlandse tekst.",
            Status = "approved", SectionRefs = "401",
        };

        PrimerDraft.Apply(doc, "Combat and showdowns", " Nieuwe Engelse tekst. ",
            " Nieuwe Nederlandse tekst. ", "402, 403", DateTimeOffset.UtcNow);

        Assert.Equal("Nieuwe Engelse tekst.", doc.Body);
        Assert.Equal("Nieuwe Nederlandse tekst.", doc.BodyNl);
        Assert.Equal("402, 403", doc.SectionRefs);
        Assert.Equal("draft", doc.Status);
    }

    [Fact]
    public void Apply_ZonderVertaling_WistDeOudeNederlandseTekst()
    {
        // Anders bleef de Nederlandse tekst van de VORIGE Engelse body als
        // tweede waarheid naast de nieuwe staan.
        var doc = new KnowledgeDoc
        {
            Kind = "primer", Topic = "combat", Title = "Combat and showdowns",
            Body = "Oude Engelse tekst.", BodyNl = "Oude Nederlandse tekst.",
            Status = "approved",
        };

        PrimerDraft.Apply(doc, "Combat and showdowns", "Nieuwe Engelse tekst.",
            null, "402", DateTimeOffset.UtcNow);

        Assert.Null(doc.BodyNl);
        Assert.Equal("draft", doc.Status);
    }
}
