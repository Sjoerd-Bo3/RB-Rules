using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        // Publieke spelbegrip-pagina (#70): alléén goedgekeurde docs, als
        // projectie zonder embeddings. Volgorde = de didactische
        // conceptvolgorde van PrimerTopics (beurtstructuur eerst, winnen
        // achteraan); topics buiten die lijst sluiten alfabetisch aan.
        // TitleNl/BodyNl (#266) zijn de Nederlandse weergave; beide mogen
        // null zijn — de pagina valt dan terug op de canonieke Engelse tekst.
        app.MapGet("/api/knowledge", async (RbRulesDbContext db) =>
        {
            var docs = await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Status == "approved")
                .Select(k => new
                {
                    k.Id, k.Kind, k.Topic, k.Title, k.Body, k.BodyNl,
                    k.SectionRefs, k.UpdatedAt,
                })
                .ToListAsync();
            var order = PrimerTopics.All
                .Select((t, i) => (t.Key, Index: i))
                .ToDictionary(x => x.Key, x => x.Index);
            return Results.Ok(docs
                .OrderBy(d => order.GetValueOrDefault(d.Topic, int.MaxValue))
                .ThenBy(d => d.Topic)
                .Select(d => new
                {
                    d.Id, d.Kind, d.Topic, d.Title,
                    TitleNl = PrimerTopics.DutchTitle(d.Topic, d.Title),
                    d.Body, d.BodyNl, d.SectionRefs, d.UpdatedAt,
                }));
        });
    }
}
