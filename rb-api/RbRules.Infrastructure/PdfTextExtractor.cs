using System.Text;
using UglyToad.PdfPig;

namespace RbRules.Infrastructure;

/// <summary>PDF → tekst via PdfPig. Regel-samenhang per pagina behouden zodat
/// sectiekoppen aan regelbegin blijven staan voor de RuleSectionParser.</summary>
public static class PdfTextExtractor
{
    public static string Extract(byte[] pdfBytes)
    {
        using var doc = PdfDocument.Open(pdfBytes);
        var sb = new StringBuilder();
        var first = true;
        foreach (var page in doc.GetPages())
        {
            // Paginagrens als form-feed: de sectie-parser leidt hieruit het
            // paginanummer af voor PDF-deeplinks (#page=N).
            if (!first) sb.Append('\f');
            first = false;

            // Woorden gegroepeerd per regel (Y-positie), regels van boven naar
            // beneden — geeft nette "601.2. Tekst…"-regels.
            var lines = page.GetWords()
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0))
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ",
                    g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            foreach (var line in lines) sb.AppendLine(line);
        }
        return sb.ToString();
    }
}
