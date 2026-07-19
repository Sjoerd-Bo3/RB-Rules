using Pgvector;
using Pgvector.EntityFrameworkCore;
using RbRules.Infrastructure.GraphRag;

namespace RbRules.Tests;

/// <summary>Fase ask-retrieval (#228-review): de β·cos-as van de entity-linker
/// (<see cref="PgVectorNodeSimilarity"/>) berekent de cosine IN-MEMORY op de
/// gematerialiseerde node-embeddings. Regressie: het eerdere pad riep
/// <c>Vector.CosineDistance</c> aan op een gematerialiseerde rij — dat is de
/// EF-translator-stub die alleen in een LINQ→SQL-boom leeft en op een echte
/// <see cref="Vector"/> een <see cref="InvalidOperationException"/> gooit, waardoor de
/// hele cos-as stil (via de try/catch) op 0 viel voor élke vraag.</summary>
public class BreinLinkingAdaptersTests
{
    private static Vector V(params float[] values) => new(values);

    [Fact]
    public void CosineSimilarity_IdentiekeVectoren_IsEen()
    {
        var v = V(0.2f, 0.5f, 0.9f, 0.1f);
        Assert.Equal(1.0, PgVectorNodeSimilarity.CosineSimilarity(v, v), 6);
    }

    [Fact]
    public void CosineSimilarity_OrthogonaleVectoren_IsNul()
    {
        Assert.Equal(0.0, PgVectorNodeSimilarity.CosineSimilarity(V(1, 0, 0), V(0, 1, 0)), 6);
    }

    [Fact]
    public void CosineSimilarity_TegengesteldeVectoren_IsMinEen()
    {
        // Vóór de klem op [0,1] bij de aanroeper: pure cosine kan negatief zijn.
        Assert.Equal(-1.0, PgVectorNodeSimilarity.CosineSimilarity(V(1, 2, 3), V(-1, -2, -3)), 6);
    }

    [Fact]
    public void CosineSimilarity_BekendeHoek_Klopt()
    {
        // (1,0)·(1,1) / (1·√2) = 1/√2 ≈ 0.70710678.
        Assert.Equal(1.0 / Math.Sqrt(2), PgVectorNodeSimilarity.CosineSimilarity(V(1, 0), V(1, 1)), 6);
    }

    [Fact]
    public void CosineSimilarity_OngelijkeDimensies_IsNul()
    {
        Assert.Equal(0.0, PgVectorNodeSimilarity.CosineSimilarity(V(1, 2, 3), V(1, 2)));
    }

    [Fact]
    public void CosineSimilarity_NulVector_IsNul()
    {
        Assert.Equal(0.0, PgVectorNodeSimilarity.CosineSimilarity(V(0, 0, 0), V(1, 2, 3)));
    }

    [Fact]
    public void CosineSimilarity_GooitNietOpGematerialiseerdeVector()
    {
        // De kern van het defect: de linker berekent op gematerialiseerde rijen.
        var a = V(0.1f, 0.2f, 0.3f);
        var b = V(0.3f, 0.2f, 0.1f);
        var ex = Record.Exception(() => PgVectorNodeSimilarity.CosineSimilarity(a, b));
        Assert.Null(ex);

        // ...precies wáár het oude pad wél op stukliep: de EF-only stub gooit
        // buiten een query-vertaling. Deze assert documenteert waarom we in-memory
        // rekenen (en faalt als een toekomstige Pgvector dit alsnog zou ondersteunen —
        // dan mag de in-memory-omweg heroverwogen worden).
        Assert.Throws<InvalidOperationException>(() => a.CosineDistance(b));
    }
}
