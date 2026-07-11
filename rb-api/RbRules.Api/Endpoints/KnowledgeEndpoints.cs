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
        app.MapGet("/api/knowledge", async (RbRulesDbContext db) =>
        {
            var docs = await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Status == "approved")
                .Select(k => new
                {
                    k.Id, k.Kind, k.Topic, k.Title, k.Body,
                    k.SectionRefs, k.UpdatedAt,
                })
                .ToListAsync();
            var order = PrimerTopics.All
                .Select((t, i) => (t.Key, Index: i))
                .ToDictionary(x => x.Key, x => x.Index);
            return Results.Ok(docs
                .OrderBy(d => order.GetValueOrDefault(d.Topic, int.MaxValue))
                .ThenBy(d => d.Topic));
        });
    }
}
