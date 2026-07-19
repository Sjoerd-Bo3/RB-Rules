using Microsoft.EntityFrameworkCore;
using RbRules.Domain.Ontology;

namespace RbRules.Infrastructure;

/// <summary>Governance-pipeline rond de PURE schema-poorten (fase 6, #230, §6). Hangt
/// de IO (Postgres = SoT, geversioneerde ontologie-historie + staging-reviewqueue) om
/// de deterministische kern (<see cref="OntologyChangeGate"/>,
/// <see cref="SchemaProposalGate"/>). Geen enkele stap laat een LLM-verdict alléén een
/// schema-wijziging harden: elke promotie vereist deterministisch bewijs én expliciete
/// review, en landt als geversioneerde migratie-Activity in de provenance-graaf.
///
/// De has-pending-ontology-poort zelf (<see cref="CheckPending"/>) is puur — code vs.
/// de checked-in <see cref="OntologyBaseline"/> — en draait zonder DB, geschikt als
/// CI-gate. De daadwerkelijke code-migratie (OntologySchema uitbreiden + baseline
/// bumpen) blijft een bewuste code-wijziging (migratie-discipline); deze service legt
/// de bijbehorende versie-rij en de reviewqueue vast — de live Neo4j-projectie van de
/// <c>:Proposed</c>-types is een integratie-follow-up (§8).</summary>
public class OntologyGovernanceService(RbRulesDbContext db)
{
    /// <summary>De has-pending-ontology-changes-poort — puur, geschikt als CI-gate.
    /// Faalt zodra <see cref="OntologySchema"/> afwijkt van de vastgelegde baseline.</summary>
    public static OntologyChangeGate.Report CheckPending() => OntologyChangeGate.Check();

    /// <summary>De laatst vastgelegde ontologie-versie (semver-gesorteerd in-memory —
    /// string-sort ≠ semver-sort). Null als er nog geen versie-rij is; de baseline
    /// (<see cref="OntologyBaseline.Version"/>) is dan de facto de startversie.</summary>
    public async Task<SemVer?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        var versions = await db.OntologyVersions.AsNoTracking()
            .Select(v => v.Version).ToListAsync(ct);
        SemVer? latest = null;
        foreach (var raw in versions)
            if (SemVer.Parse(raw) is { } v && (latest is null || v > latest.Value))
                latest = v;
        return latest;
    }

    /// <summary>Legt een toegepaste ontologie-versie vast (de migratie-Activity). Borgt
    /// monotone toename t.o.v. de laatste rij én de baseline — een versie mag nooit
    /// terugvallen. De structuur-vingerafdruk wordt uit de HUIDIGE
    /// <see cref="OntologySchema"/> genomen (de code die na de migratie geldt).</summary>
    public async Task<OntologyVersionRecord> RecordVersionAsync(
        SemVer version, OntologyBumpKind bumpKind, string notes, string runId,
        CancellationToken ct = default)
    {
        var latest = await GetLatestVersionAsync(ct) ?? OntologyBaseline.Version;
        if (version <= latest)
            throw new InvalidOperationException(
                $"Ontologie-versie {version} moet groter zijn dan de laatst vastgelegde {latest}.");

        var record = new OntologyVersionRecord
        {
            Version = version.ToString(),
            Fingerprint = OntologySnapshot.CurrentFingerprint(),
            BumpKind = bumpKind.ToString(),
            Notes = notes,
            RunId = runId,
        };
        db.OntologyVersions.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>Registreert een schema-evolutie-voorstel in de staging-namespace
    /// (idempotent op (soort, naam) — een dubbel voorstel voor hetzelfde type geeft de
    /// bestaande rij terug). Het voorstel start op <c>proposed</c>: retrieval-zichtbaar
    /// met lage weging, kan niets harden.</summary>
    public async Task<SchemaProposal> ProposeAsync(
        SchemaProposalKind kind, string proposedName, string memo, string runId,
        string? parentType = null, int officialCardCount = 0,
        string? ruleSectionRef = null, CancellationToken ct = default)
    {
        var kindStr = kind.ToString();
        var existing = await db.SchemaProposals
            .FirstOrDefaultAsync(p => p.Kind == kindStr && p.ProposedName == proposedName, ct);
        if (existing is not null) return existing;

        var proposal = new SchemaProposal
        {
            Kind = kindStr,
            ProposedName = proposedName,
            ParentType = parentType,
            OfficialCardCount = officialCardCount,
            HasRuleSectionEvidence = !string.IsNullOrWhiteSpace(ruleSectionRef),
            RuleSectionRef = ruleSectionRef,
            Memo = memo,
            BumpKind = (kind == SchemaProposalKind.Disjointness
                ? OntologyBumpKind.Major : OntologyBumpKind.Minor).ToString(),
            RunId = runId,
        };
        db.SchemaProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
        return proposal;
    }

    /// <summary>Toetst een voorstel tegen de deterministische promotie-poort (≥N
    /// officiële kaarten ÉN een verankerende sectie). Verandert niets — alleen de
    /// review-geschiktheid.</summary>
    public async Task<SchemaProposalGate.Result> EvaluateProposalAsync(
        long proposalId, int minOfficialCards = SchemaProposalGate.DefaultMinOfficialCards,
        CancellationToken ct = default)
    {
        var proposal = await db.SchemaProposals.FindAsync([proposalId], ct)
            ?? throw new InvalidOperationException($"Schema-voorstel {proposalId} bestaat niet.");
        return SchemaProposalGate.Evaluate(proposal, minOfficialCards);
    }

    /// <summary>Keurt een voorstel goed (expliciete beheerders-review). Vereist dat de
    /// deterministische poort al open staat — anders kan de review het bewijs niet
    /// overrulen (rode draad #236: geen schema-wijziging zonder deterministisch bewijs).
    /// Zet <c>approved</c>; de daadwerkelijke migratie is een aparte stap.</summary>
    public async Task<SchemaProposal> ApproveProposalAsync(
        long proposalId, string reviewedBy, string note,
        int minOfficialCards = SchemaProposalGate.DefaultMinOfficialCards,
        CancellationToken ct = default)
    {
        var proposal = await db.SchemaProposals.FindAsync([proposalId], ct)
            ?? throw new InvalidOperationException($"Schema-voorstel {proposalId} bestaat niet.");
        if (proposal.Status != SchemaProposalStatus.Proposed)
            throw new InvalidOperationException(
                $"Alleen een voorstel in '{SchemaProposalStatus.Proposed}' kan goedgekeurd worden (nu '{proposal.Status}').");

        var gate = SchemaProposalGate.Evaluate(proposal, minOfficialCards);
        if (!gate.EligibleForReview)
            throw new InvalidOperationException(
                $"Voorstel haalt de deterministische bewijs-drempel niet: {gate.Reason}");

        proposal.Status = SchemaProposalStatus.Approved;
        proposal.ReviewedBy = reviewedBy;
        proposal.ReviewNote = note;
        proposal.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return proposal;
    }

    /// <summary>Wijst een voorstel af. Blijft als tombstone bestaan (audit-spoor).</summary>
    public async Task<SchemaProposal> RejectProposalAsync(
        long proposalId, string reviewedBy, string note, CancellationToken ct = default)
    {
        var proposal = await db.SchemaProposals.FindAsync([proposalId], ct)
            ?? throw new InvalidOperationException($"Schema-voorstel {proposalId} bestaat niet.");
        proposal.Status = SchemaProposalStatus.Rejected;
        proposal.ReviewedBy = reviewedBy;
        proposal.ReviewNote = note;
        proposal.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return proposal;
    }

    /// <summary>Markeert een goedgekeurd voorstel als gemigreerd en legt de bijbehorende
    /// versie-rij vast. De caller levert de versie die na de code-migratie (OntologySchema
    /// uitbreiden + baseline bumpen) geldt — deze service dwingt de code-migratie NIET af
    /// (migratie-discipline blijft handmatig), maar borgt de administratieve koppeling
    /// voorstel↔versie.</summary>
    public async Task<OntologyVersionRecord> MigrateProposalAsync(
        long proposalId, SemVer landedVersion, string runId, CancellationToken ct = default)
    {
        var proposal = await db.SchemaProposals.FindAsync([proposalId], ct)
            ?? throw new InvalidOperationException($"Schema-voorstel {proposalId} bestaat niet.");
        if (proposal.Status != SchemaProposalStatus.Approved)
            throw new InvalidOperationException(
                $"Alleen een goedgekeurd voorstel kan gemigreerd worden (nu '{proposal.Status}').");

        var record = await RecordVersionAsync(
            landedVersion, proposal.BumpKindEnum,
            $"{proposal.Kind} {proposal.ProposedName}: {proposal.Memo}", runId, ct);

        proposal.Status = SchemaProposalStatus.Migrated;
        proposal.MigratedInVersion = landedVersion.ToString();
        await db.SaveChangesAsync(ct);
        return record;
    }
}
