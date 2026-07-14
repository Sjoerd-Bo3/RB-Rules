using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Compleetheidssignaal voor het bron-dossier (#171): pure logica,
/// geen I/O — de vier statusgevallen die de dossier-service en het
/// kennis-gaten-rapport allebei op run_log/Document-gegevens toepassen.</summary>
public class SourceDossierCompletenessTests
{
    [Fact]
    public void Evaluate_NooitGescand_GeenScanRegel()
    {
        var status = SourceDossierCompleteness.Evaluate(
            lastScanStatus: null, anyStepFailed: false, anyStepPending: false, opbrengstTotaal: 0);

        Assert.Equal(SourceDossierCompleteness.NooitGescand, status);
    }

    [Fact]
    public void Evaluate_LaatsteScanMislukt_Onvolledig()
    {
        var status = SourceDossierCompleteness.Evaluate(
            lastScanStatus: "error", anyStepFailed: false, anyStepPending: false, opbrengstTotaal: 5);

        Assert.Equal(SourceDossierCompleteness.Onvolledig, status);
    }

    [Fact]
    public void Evaluate_ScanOkMaarVervolgstapMislukt_Onvolledig()
    {
        var status = SourceDossierCompleteness.Evaluate(
            lastScanStatus: "ok", anyStepFailed: true, anyStepPending: false, opbrengstTotaal: 3);

        Assert.Equal(SourceDossierCompleteness.Onvolledig, status);
    }

    [Fact]
    public void Evaluate_ScanOkMaarVervolgstapHangtNog_Onvolledig()
    {
        var status = SourceDossierCompleteness.Evaluate(
            lastScanStatus: "unchanged", anyStepFailed: false, anyStepPending: true, opbrengstTotaal: 0);

        Assert.Equal(SourceDossierCompleteness.Onvolledig, status);
    }

    [Fact]
    public void Evaluate_ScanOkGeenVervolgstapNietsOpgeleverd_Leeg()
    {
        var status = SourceDossierCompleteness.Evaluate(
            lastScanStatus: "unchanged", anyStepFailed: false, anyStepPending: false, opbrengstTotaal: 0);

        Assert.Equal(SourceDossierCompleteness.Leeg, status);
    }

    [Fact]
    public void Evaluate_ScanOkGeenVervolgstapMetOpbrengst_Volledig()
    {
        var status = SourceDossierCompleteness.Evaluate(
            lastScanStatus: "new", anyStepFailed: false, anyStepPending: false, opbrengstTotaal: 1);

        Assert.Equal(SourceDossierCompleteness.Volledig, status);
    }

    [Theory]
    [InlineData(SourceDossierCompleteness.NooitGescand)]
    [InlineData(SourceDossierCompleteness.Onvolledig)]
    [InlineData(SourceDossierCompleteness.Leeg)]
    [InlineData(SourceDossierCompleteness.Volledig)]
    public void Note_ElkeStatus_GeeftNietLegeToelichting(string status)
    {
        Assert.False(string.IsNullOrWhiteSpace(SourceDossierCompleteness.Note(status)));
    }
}
