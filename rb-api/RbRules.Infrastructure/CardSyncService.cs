using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record CardSyncResult(
    int Sets, int Cards, string Source,
    int MergedDuplicates = 0, int NormalizedIds = 0, int NormalizedNames = 0,
    int SupplementCards = 0, string? SupplementSource = null)
{
    /// <summary>Reparatie-naslag voor run_log/job-detail (#144); leeg als er
    /// niets te repareren viel.</summary>
    public string RepairSummary =>
        MergedDuplicates + NormalizedIds + NormalizedNames == 0
            ? ""
            : $", reparatie: {MergedDuplicates} dubbelen samengevoegd, " +
              $"{NormalizedIds} id-vormen en {NormalizedNames} namen genormaliseerd";

    /// <summary>Compact bronlabel voor run_log-ref en logregels:
    /// "riot" of "riot+riftcodex".</summary>
    public string SourceLabel =>
        SupplementSource is null ? Source : $"{Source}+{SupplementSource}";

    /// <summary>Telling per bron voor job-detail/run_log (#150):
    /// "1083 kaarten via riot + 141 aanvullend via riftcodex".</summary>
    public string CardsSummary => SupplementSource is null
        ? $"{Cards} kaarten via {Source}"
        : $"{Cards} kaarten via {Source} + {SupplementCards} aanvullend via {SupplementSource}";
}

public record CardRepairResult(int MergedDuplicates, int NormalizedIds, int NormalizedNames);

/// <summary>Kaart-sync: de officiële Riot-gallery is leidend (onvoorwaardelijke
/// upsert), de riftcodex-API vult daarna alleen aan — extra kaarten (JDG-promo's)
/// en set-metadata die de gallery niet levert (#150). Idempotente upsert; nieuwe
/// sets komen automatisch mee. Riftcodex-bronvormen (ster-id's, streepjes-namen)
/// worden bij binnenkomst genormaliseerd naar de Riot-vorm (#144), en een vaste
/// reparatiestap voegt eerder ontstane dubbelen samen.</summary>
public class CardSyncService(RbRulesDbContext db, HttpClient http)
{
    private const string RiftcodexBase = "https://api.riftcodex.com";
    private const string RiotGallery = "https://playriftbound.com/en-us/card-gallery/";

    public async Task<CardSyncResult> SyncAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        // Opruimen: set-facetten die eerdere versies per abuis als kaart
        // importeerden (id zonder '-', bijv. 'VEN'/'OGN'). String-overload:
        // Npgsql vertaalt Contains(char) niet naar SQL. Tracked delete i.p.v.
        // ExecuteDelete: dit matcht vrijwel nooit rijen, en zo draait de hele
        // sync ook onder de InMemory-testprovider (#150-servicetests).
        db.Cards.RemoveRange(await db.Cards
            .Where(c => !c.RiftboundId.Contains("-")).ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        // Reparatie vóór de upsert (#144): de bronwissel van 2026-07-12 liet
        // ster-id's en streepjes-namen achter; de genormaliseerde adapter
        // hieronder zou die anders als "nieuwe" kaarten dubbel aanleggen.
        progress?.Invoke("bronvormen repareren (riftcodex-dubbelen, #144)");
        var repair = await RepairSourceFormsAsync(ct);

        var mode = Environment.GetEnvironmentVariable("CARD_SOURCE") ?? "auto";
        var result = mode switch
        {
            // Expliciete overrides: precies één bron, uitval = jobfout.
            "riot" => await SyncFromRiotAsync(progress, ct),
            "riftcodex" => await SyncFromRiftcodexAsync(progress, ct),
            _ => await SyncAutoAsync(progress, ct),
        };

        progress?.Invoke("varianten groeperen (alt-art/promo/herdruk)");
        await RegroupVariantsAsync(ct);
        return result with
        {
            MergedDuplicates = repair.MergedDuplicates,
            NormalizedIds = repair.NormalizedIds,
            NormalizedNames = repair.NormalizedNames,
        };
    }

    /// <summary>Auto-modus (#150): de officiële Riot-gallery eerst — namen en
    /// kaartvelden zijn leidend (kennislagen: officieel > community) — en
    /// riftcodex daarna alléén als aanvulling. Riot-uitval ⇒ riftcodex alleen;
    /// riftcodex-uitval ná een geslaagde Riot-run is data (run_log-info),
    /// geen jobfout — het Riot-resultaat staat dan al.</summary>
    private async Task<CardSyncResult> SyncAutoAsync(
        Action<string>? progress, CancellationToken ct)
    {
        CardSyncResult riot;
        try
        {
            riot = await SyncFromRiotAsync(progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress?.Invoke("Riot-gallery niet bereikbaar — overschakelen naar Riftcodex");
            return await SyncFromRiftcodexAsync(progress, ct);
        }

        // De Riot-pass heeft zijn wijzigingen al gesaved; de aanvul-pass laadt
        // de kaartenset vers en heeft dus het actuele naambewijs (verse
        // komma-namen) voor de normalisatie van nieuwe riftcodex-kaarten.
        try
        {
            var extra = await SyncFromRiftcodexAsync(progress, ct, supplementOnly: true);
            return riot with { SupplementCards = extra.Cards, SupplementSource = extra.Source };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress?.Invoke("Riftcodex niet bereikbaar — Riot-resultaat staat, aanvulling overgeslagen");
            db.RunLogs.Add(new RunLog
            {
                Kind = "cards", Ref = "riftcodex-aanvulling", Status = "info",
                Detail = $"aanvulling overgeslagen na geslaagde Riot-sync: {ex.Message}",
            });
            await db.SaveChangesAsync(ct);
            return riot;
        }
    }

    /// <summary>Eenmalige reparatie van de riftcodex-bronwissel (#144), als
    /// idempotente vaste stap in de sync (tweede run = 0 wijzigingen — géén
    /// aparte datamigratie). Drie sporen, in één transactie:
    /// 1. dubbelen op (set, collector-nummer, variant-suffix) — dat is id-
    ///    gelijkheid na ster-normalisatie — samenvoegen; de rij met de
    ///    canonieke vorm wint en alle verwijzingen hangen mee om;
    /// 2. ster-id's zonder canonieke tegenhanger hernoemen, zodat de
    ///    genormaliseerde adapter ze blijft vinden;
    /// 3. streepjes-namen met bewijs terugzetten naar de komma-vorm.
    /// Neo4j hangt niet mee om: de graph-sync verwijdert verdwenen kaart-
    /// knopen zelf ("NOT c.id IN $ids" + DETACH DELETE) en draait in de
    /// setrelease-keten en "Alles bijwerken" ná de kaartenstap.</summary>
    public async Task<CardRepairResult> RepairSourceFormsAsync(CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var cards = await db.Cards.ToListAsync(ct);
        var byId = cards.ToDictionary(c => c.RiftboundId);
        var refs = new CardReferences(
            await db.CardInteractions.ToListAsync(ct),
            await db.SimilarityExplanations.ToListAsync(ct),
            await db.BanEntries.Where(b => b.CardRiftboundId != null).ToListAsync(ct),
            await db.Errata.Where(e => e.CardRiftboundId != null).ToListAsync(ct),
            await db.Corrections.Where(c => c.Scope == "card").ToListAsync(ct),
            await db.Relations
                .Where(r => r.FromRef.StartsWith("card:") || r.ToRef.StartsWith("card:"))
                .ToListAsync(ct));

        var cardClaims = await db.Claims.Where(c => c.TopicType == "card").ToListAsync(ct);
        int merged = 0, normalizedIds = 0;
        foreach (var card in cards.Where(c => RiftboundIds.Normalize(c.RiftboundId) != c.RiftboundId))
        {
            var canonicalId = RiftboundIds.Normalize(card.RiftboundId);
            if (byId.TryGetValue(canonicalId, out var winner))
            {
                merged++; // dubbel: de rij met de canonieke vorm wint
                // Claims verwijzen kaarten op naam — met de rij verdwijnt ook
                // de riftcodex-naam; hang ze om naar de winnende naam.
                foreach (var claim in cardClaims.Where(cl => cl.TopicRef == card.Name))
                    claim.TopicRef = winner.Name;
            }
            else
            {
                // Alleen de riftcodex-vorm bestaat: id-vorm normaliseren.
                // RiftboundId is de sleutel — dus kopie erbij, oude rij weg.
                var moved = CopyWithId(card, canonicalId);
                db.Cards.Add(moved);
                byId[canonicalId] = moved;
                normalizedIds++;
            }
            RepointReferences(card.RiftboundId, canonicalId, byId.Values, refs);
            db.Cards.Remove(card);
            byId.Remove(card.RiftboundId);
        }

        // Naamreparatie: streepjes-namen waarvoor de komma-basisnaam al als
        // kaart bekend is (aantoonbaar dezelfde kaart). Zonder bewijs blijft
        // de naam staan — de eerstvolgende Riot-sync is dan de waarheid.
        var known = RiftcodexCardMapper.CommaBaseNames(byId.Values.Select(c => c.Name));
        var normalizedNames = 0;
        foreach (var card in byId.Values.Where(c => RiftcodexCardMapper.HasDashName(c.Name)))
        {
            var commaName = RiftcodexCardMapper.NormalizeName(card.Name, known);
            if (commaName == card.Name) continue;
            // Claims verwijzen kaarten op naam (TopicRef) — mee omhangen.
            foreach (var claim in cardClaims.Where(cl => cl.TopicRef == card.Name))
                claim.TopicRef = commaName;
            card.Name = commaName;
            // De naam zit in de embeddingtekst (CardText.Compose):
            // leegmaken zodat de embed-pijplijn de kaart opnieuw oppakt.
            card.Embedding = null;
            card.EmbeddingModel = null;
            normalizedNames++;
        }

        if (merged + normalizedIds + normalizedNames > 0)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "cards", Ref = "bronvorm-reparatie", Status = "ok",
                Detail = $"{merged} dubbelen samengevoegd, {normalizedIds} id-vormen " +
                         $"en {normalizedNames} namen genormaliseerd (#144)",
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(merged, normalizedIds, normalizedNames);
    }

    /// <summary>Alle tabellen die op riftbound_id (of kaartnaam) verwijzen —
    /// één keer geladen, zodat de reparatie zonder query-per-rij omhangt.</summary>
    private sealed record CardReferences(
        List<CardInteraction> Interactions,
        List<SimilarityExplanation> Explanations,
        List<BanEntry> Bans,
        List<Erratum> Errata,
        List<Correction> Corrections,
        List<Relation> Relations)
    {
        public HashSet<(string, string)> InteractionPairs { get; } =
            [.. Interactions.Select(i => (i.CardAId, i.CardBId))];
        public HashSet<(string, string)> ExplanationPairs { get; } =
            [.. Explanations.Select(e => (e.CardAId, e.CardBId))];
        public HashSet<(string, string, string)> RelationKeys { get; } =
            [.. Relations.Select(r => (r.FromRef, r.ToRef, r.Kind))];
    }

    private void RepointReferences(
        string oldId, string newId, IEnumerable<Card> cards, CardReferences refs)
    {
        foreach (var c in cards.Where(c => c.VariantOf == oldId))
            c.VariantOf = c.RiftboundId == newId ? null : newId;

        foreach (var b in refs.Bans.Where(b => b.CardRiftboundId == oldId))
            b.CardRiftboundId = newId;
        foreach (var e in refs.Errata.Where(e => e.CardRiftboundId == oldId))
            e.CardRiftboundId = newId;
        foreach (var c in refs.Corrections.Where(c => c.Ref == oldId))
            c.Ref = newId;

        // Interactie- en uitlegparen zijn geordend en uniek: her-punten kan
        // botsen met een bestaand paar of tot een zelf-paar leiden — dan
        // vervalt de rij (het canonieke paar bestaat al of is betekenisloos).
        foreach (var i in refs.Interactions
                     .Where(i => i.CardAId == oldId || i.CardBId == oldId).ToList())
        {
            refs.InteractionPairs.Remove((i.CardAId, i.CardBId));
            var pair = CardText.OrderedPair(
                i.CardAId == oldId ? newId : i.CardAId,
                i.CardBId == oldId ? newId : i.CardBId);
            if (pair.A == pair.B || !refs.InteractionPairs.Add(pair))
            {
                db.CardInteractions.Remove(i);
                refs.Interactions.Remove(i);
                continue;
            }
            (i.CardAId, i.CardBId) = pair;
        }
        foreach (var e in refs.Explanations
                     .Where(e => e.CardAId == oldId || e.CardBId == oldId).ToList())
        {
            refs.ExplanationPairs.Remove((e.CardAId, e.CardBId));
            var pair = CardText.OrderedPair(
                e.CardAId == oldId ? newId : e.CardAId,
                e.CardBId == oldId ? newId : e.CardBId);
            if (pair.A == pair.B || !refs.ExplanationPairs.Add(pair))
            {
                db.SimilarityExplanations.Remove(e);
                refs.Explanations.Remove(e);
                continue;
            }
            (e.CardAId, e.CardBId) = pair;
        }

        // Dynamische relaties dragen kaarten als BrainRef ("card:<id>");
        // (FromRef, ToRef, Kind) is uniek — botsing = rij vervalt.
        var oldRef = BrainRef.Card(oldId).Format();
        var newRef = BrainRef.Card(newId).Format();
        foreach (var r in refs.Relations
                     .Where(r => r.FromRef == oldRef || r.ToRef == oldRef).ToList())
        {
            refs.RelationKeys.Remove((r.FromRef, r.ToRef, r.Kind));
            var from = r.FromRef == oldRef ? newRef : r.FromRef;
            var to = r.ToRef == oldRef ? newRef : r.ToRef;
            if (from == to || !refs.RelationKeys.Add((from, to, r.Kind)))
            {
                db.Relations.Remove(r);
                refs.Relations.Remove(r);
                continue;
            }
            (r.FromRef, r.ToRef) = (from, to);
        }
    }

    private static Card CopyWithId(Card c, string newId) => new()
    {
        RiftboundId = newId,
        Name = c.Name, Type = c.Type, Supertype = c.Supertype, Rarity = c.Rarity,
        Domains = c.Domains, Energy = c.Energy, Might = c.Might, Power = c.Power,
        SetId = c.SetId, SetLabel = c.SetLabel, CollectorNumber = c.CollectorNumber,
        TextPlain = c.TextPlain, ImageUrl = c.ImageUrl, Tags = c.Tags,
        Mechanics = c.Mechanics, Triggers = c.Triggers, Effects = c.Effects,
        Embedding = c.Embedding, EmbeddingModel = c.EmbeddingModel,
        VariantOf = c.VariantOf, UpdatedAt = c.UpdatedAt,
        // Presentatievelden (#270) horen bij dezelfde kaart en moeten dus
        // meeverhuizen — anders raakt een id-reparatie ze stil kwijt.
        PublicCode = c.PublicCode, Illustrator = c.Illustrator,
        MightBonus = c.MightBonus, EffectPlain = c.EffectPlain, Flags = c.Flags,
        ImageWidth = c.ImageWidth, ImageHeight = c.ImageHeight,
        ImageColorPrimary = c.ImageColorPrimary,
        ImageColorSecondary = c.ImageColorSecondary,
        ImageAltText = c.ImageAltText,
    };

    /// <summary>Groepeert printings van dezelfde kaart: één canonieke kaart,
    /// de rest (alt-art/showcase/promo/herdruk) verwijst ernaar via VariantOf.
    /// Groepering op basisnaam: Riot noemt varianten "Naam (Alternate Art)",
    /// "(Signature)", "(Overnumbered)" — die horen bij dezelfde kaart. Nieuwe
    /// printings van bestaande namen (set 7-scenario, #57) groeperen zo
    /// automatisch; de keuze van de canonieke printing is gepind in
    /// VariantGrouping.ChooseCanonical zodat een herdruk geen churn geeft.</summary>
    private async Task RegroupVariantsAsync(CancellationToken ct)
    {
        var cards = await db.Cards.ToListAsync(ct);
        foreach (var group in cards.GroupBy(c => CardText.BaseName(c.Name)))
        {
            var members = group.ToList();
            var canonical = VariantGrouping.ChooseCanonical(members);
            foreach (var c in members)
            {
                var variantOf = ReferenceEquals(c, canonical) ? null : canonical.RiftboundId;
                if (c.VariantOf != variantOf) c.VariantOf = variantOf;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Alle bestaande kaarten één keer laden (review-fix #43: geen
    /// FindAsync per kaart — dat waren ~1250 losse SELECT's per sync).</summary>
    private async Task<Dictionary<string, Card>> LoadExistingAsync(CancellationToken ct) =>
        await db.Cards.ToDictionaryAsync(c => c.RiftboundId, ct);

    /// <summary>Volledige riftcodex-sync, of — met <paramref name="supplementOnly"/>
    /// (#150) — alleen de aanvulling op de leidende Riot-pass: kaarten die al
    /// bestaan blijven volledig onaangeraakt (geen veld-overschrijving, geen
    /// embedding-churn), nieuwe kaarten (JDG-promo's) en set-metadata (échte
    /// setnamen/releasedatums, die de Riot-gallery niet levert) komen erbij.
    /// Cards in het resultaat telt dan alleen de aangevulde kaarten.</summary>
    private async Task<CardSyncResult> SyncFromRiftcodexAsync(
        Action<string>? progress, CancellationToken ct, bool supplementOnly = false)
    {
        progress?.Invoke(supplementOnly
            ? "aanvulling ophalen bij Riftcodex (extra kaarten en set-metadata)"
            : "setlijst ophalen bij Riftcodex");
        var existing = await LoadExistingAsync(ct);
        // Bewijs voor naamnormalisatie (#144): welke komma-basisnamen kennen
        // we al — alleen dán mag "Naam - Epithet" de komma-vorm worden.
        var knownCommaNames = RiftcodexCardMapper.CommaBaseNames(
            existing.Values.Select(c => c.Name));
        var setsNode = await GetJsonAsync($"{RiftcodexBase}/sets?page=1&size=100", ct);
        var sets = AsList(setsNode);
        var setIds = new List<string>();
        foreach (var s in sets.OfType<JsonObject>())
        {
            var setId = s["set_id"]?.GetValue<string>();
            if (setId is null) continue;
            await UpsertSetAsync(setId.ToUpperInvariant(), s["name"]?.GetValue<string>() ?? setId,
                s["card_count"]?.GetValue<int?>(), ParsePublishedOn(s["published_on"]), ct);
            setIds.Add(setId);
        }

        var total = 0;
        foreach (var setId in setIds)
        {
            for (var page = 1; page <= 200; page++)
            {
                progress?.Invoke($"set {setId}: pagina {page} ophalen " +
                    $"({total} kaarten {(supplementOnly ? "aangevuld" : "verwerkt")})");
                var node = await GetJsonAsync(
                    $"{RiftcodexBase}/cards?set_id={Uri.EscapeDataString(setId)}&page={page}&size=100", ct);
                var items = AsList(node);
                if (items.Count == 0) break;
                foreach (var c in items.OfType<JsonObject>())
                {
                    var card = RiftcodexCardMapper.MapCard(c, setId);
                    if (card is null) continue;
                    // Een bestaande naam wint altijd (ook échte Riot-
                    // streepjesnamen zoals de OGS-starters — anders
                    // flip-flopt de naam per bronwissel); riftcodex vult
                    // alleen gaten, zo mogelijk genormaliseerd. Dash-
                    // artefacten herstelt RepairSourceFormsAsync.
                    card.Name = RiftcodexCardMapper.ResolveName(
                        existing.TryGetValue(card.RiftboundId, out var have) ? have.Name : null,
                        card.Name, knownCommaNames);
                    // Aanvullend (#150/#270): nieuwe kaarten (JDG-promo's) komen
                    // erbij en lege velden van bestaande kaarten worden gevuld
                    // — gevulde Riot-velden blijven gegarandeerd onaangeraakt
                    // (CardMerge). Is riftcodex de énige bron (Riot plat), dan
                    // is hij leidend en schrijft hij wél door; anders zou de
                    // kaartenset bevriezen zolang Riot uit staat.
                    var added = await UpsertCardAsync(card, existing, !supplementOnly, ct);
                    // Aanvullend telt alleen échte extra kaarten ("141
                    // aanvullend via riftcodex"); als enige bron telt elke
                    // verwerkte kaart mee.
                    if (added || !supplementOnly) total++;
                }
                if (items.Count < 100) break;
            }
        }
        await db.SaveChangesAsync(ct);
        return new(setIds.Count, total, "riftcodex");
    }

    private async Task<CardSyncResult> SyncFromRiotAsync(
        Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("officiële card-gallery ophalen (playriftbound.com)");
        var html = await GetStringAsync(RiotGallery, ct);
        var buildId = RiotCardMapper.ExtractBuildId(html)
            ?? throw new InvalidOperationException("Riot build-id niet gevonden");
        var json = await GetJsonAsync(
            $"https://playriftbound.com/_next/data/{buildId}/en-us/card-gallery.json", ct);
        var cards = RiotCardMapper.ParseGallery(json?["pageProps"] ?? new JsonObject());

        progress?.Invoke($"{cards.Count} kaarten verwerken");
        var existing = await LoadExistingAsync(ct);
        var setIds = new HashSet<string>();
        foreach (var card in cards)
        {
            // De officiële gallery is leidend: onvoorwaardelijk schrijven.
            await UpsertCardAsync(card, existing, leading: true, ct);
            if (card.SetId is not null) setIds.Add(card.SetId);
        }
        // De Riot-gallery levert geen setnamen of releasedatums bij de
        // set-facetten — alleen de set-code is hier bekend.
        foreach (var sid in setIds) await UpsertSetAsync(sid, sid, null, null, ct);
        await db.SaveChangesAsync(ct);
        return new(setIds.Count, cards.Count, "riot");
    }

    /// <summary>Idempotente upsert. <paramref name="leading"/> bepaalt de
    /// voorrang (zie <see cref="CardMerge"/>): de leidende bron schrijft
    /// onvoorwaardelijk, een aanvullende vult alleen lege velden.
    /// Geeft terug of de kaart NIEUW was — dat is wat "X aanvullend via
    /// riftcodex" telt; een gevuld gat is geen extra kaart.</summary>
    private async Task<bool> UpsertCardAsync(
        Card card, Dictionary<string, Card> known, bool leading, CancellationToken ct)
    {
        if (!known.TryGetValue(card.RiftboundId, out var existing))
        {
            db.Cards.Add(card);
            known[card.RiftboundId] = card;
            return true;
        }
        var changes = CardMerge.Apply(existing, card, leading);
        // Naam- óf tekstwijziging invalideert de embedding — beide zitten in
        // de embeddingtekst (CardText.Compose); zo pakt de embed-pijplijn een
        // door Riot herstelde naam ("Darius - Trifarian" → "Darius, Trifarian",
        // #150) automatisch op. Alleen bij échte wijziging — geen churn.
        if (changes.NameChanged || changes.TextChanged)
        {
            existing.Embedding = null;
            existing.EmbeddingModel = null;
        }
        // Tekstwijziging (errata!) maakt ook de gecachete gelijkenis-uitleg
        // over deze kaart achterhaald.
        if (changes.TextChanged)
        {
            await db.SimilarityExplanations
                .Where(e => e.CardAId == card.RiftboundId || e.CardBId == card.RiftboundId)
                .ExecuteDeleteAsync(ct);
        }
        // UpdatedAt is "wanneer veranderde deze kaart", niet "wanneer draaide
        // de sync" — anders lijkt na elke run de hele set gewijzigd.
        if (changes.Any) existing.UpdatedAt = DateTimeOffset.UtcNow;
        return false;
    }

    /// <summary>Riftcodex levert published_on als ISO-datetime
    /// ("2026-07-31T00:00:00") — basis voor de set-legaliteit (#22).</summary>
    private static DateOnly? ParsePublishedOn(JsonNode? node) =>
        node?.GetValue<string>() is { } s &&
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateOnly.FromDateTime(dt)
            : null;

    private async Task UpsertSetAsync(
        string setId, string name, int? cardCount, DateOnly? publishedOn, CancellationToken ct)
    {
        var existing = await db.CardSets.FindAsync([setId], ct);
        if (existing is null)
        {
            db.CardSets.Add(new CardSet
            {
                SetId = setId, Name = name, CardCount = cardCount, PublishedOn = publishedOn,
            });
            return;
        }
        // De Riot-fallback kent alleen de set-code (name == setId, geen datum):
        // eerder gesyncte échte namen/releasedatums nooit overschrijven met "onbekend".
        if (name != setId) existing.Name = name;
        existing.CardCount = cardCount ?? existing.CardCount;
        existing.PublishedOn = publishedOn ?? existing.PublishedOn;
        existing.SyncedAt = DateTimeOffset.UtcNow;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", IngestService.BrowserUserAgent);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct) =>
        JsonNode.Parse(await GetStringAsync(url, ct));

    private static List<JsonNode?> AsList(JsonNode? node)
    {
        if (node is JsonArray arr) return [.. arr];
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "data", "items", "results", "cards", "sets" })
                if (obj[key] is JsonArray a) return [.. a];
        }
        return [];
    }
}
