using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Domain.Tests.MigrationCompletion;

/// <summary>
/// AB-I8-010/AB-I8-011/AB-I8-012 — <see cref="MigrationCompletionCriterionAttestation"/>: bloqueio estrutural
/// contra atestar um critério SystemDerived (reconciliação/resultados do provider) OU EvidenceDerived
/// (disposition de fontes/parts, publicação WORM, ausência de credencial temporária, tratamento de
/// usuários/inativos — técnicos/objetivos, sem store canônico suficiente, AB-I8-011/AB-I8-012) e contra
/// aprovação implícita (Pass sem evidência real) — escopo obrigatório item 8: "aprovação do cliente deve ser
/// evidência auditável; ausência de evidência não pode virar aprovação implícita".
/// </summary>
public sealed class MigrationCompletionCriterionAttestationTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly ProjectId Project = new(Guid.NewGuid());
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("COMPLETION.RECONCILIATION_CLOSED")]
    [InlineData("COMPLETION.PROVIDER_RESULTS_COLLECTED")]
    public void CreateThrowsForASystemDerivedCriterion(string systemDerivedCriterionId)
    {
        Assert.Throws<MigrationCompletionAttestationNotAllowedException>(() => MigrationCompletionCriterionAttestation.Create(
            Tenant, Project, new MigrationCompletionCriterionId(systemDerivedCriterionId), 1, ReadinessControlStatus.Pass,
            ReadinessEvidenceReference.Attested(SomeHash, "manual override attempt"), reasonCode: string.Empty, "attacker",
            "Administrator", CorrelationId.New(), Now));
    }

    // AB-I8-011/AB-I8-012: critérios tecnicamente objetivos sem store canônico suficiente neste repositório —
    // uma atestação humana NUNCA pode substituir a ausência desse store, mesmo por um ator com o papel mais
    // privilegiado (mesmo bloqueio estrutural de um critério SystemDerived).
    [Theory]
    [InlineData("COMPLETION.SOURCE_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.PARTS_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM")]
    [InlineData("COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL")]
    [InlineData("COMPLETION.USERS_INACTIVE_HANDLED")]
    public void CreateThrowsForAnEvidenceDerivedCriterion(string evidenceDerivedCriterionId)
    {
        Assert.Throws<MigrationCompletionAttestationNotAllowedException>(() => MigrationCompletionCriterionAttestation.Create(
            Tenant, Project, new MigrationCompletionCriterionId(evidenceDerivedCriterionId), 1, ReadinessControlStatus.Pass,
            ReadinessEvidenceReference.Attested(SomeHash, "manual override attempt"), reasonCode: string.Empty, "attacker",
            "Administrator", CorrelationId.New(), Now));
    }

    [Fact]
    public void CreateThrowsForAnUnknownCriterion()
    {
        Assert.Throws<MigrationCompletionAttestationNotAllowedException>(() => MigrationCompletionCriterionAttestation.Create(
            Tenant, Project, new MigrationCompletionCriterionId("COMPLETION.NOT_A_REAL_CRITERION"), 1, ReadinessControlStatus.Pass,
            ReadinessEvidenceReference.Attested(SomeHash, "fabricated"), reasonCode: string.Empty, "attacker", "Administrator",
            CorrelationId.New(), Now));
    }

    [Fact]
    public void CreateThrowsWhenPassIsDeclaredWithoutRealEvidence()
    {
        Assert.Throws<ArgumentException>(() => MigrationCompletionCriterionAttestation.Create(
            Tenant, Project, new MigrationCompletionCriterionId("COMPLETION.CUSTOMER_FINAL_APPROVAL"), 1, ReadinessControlStatus.Pass,
            ReadinessEvidenceReference.None, reasonCode: string.Empty, "approver-1", "Approver", CorrelationId.New(), Now));
    }

    [Fact]
    public void CreateSucceedsForAnAttestedCriterionWithRealEvidence()
    {
        var attestation = MigrationCompletionCriterionAttestation.Create(
            Tenant, Project, new MigrationCompletionCriterionId("COMPLETION.CUSTOMER_FINAL_APPROVAL"), 1, ReadinessControlStatus.Pass,
            ReadinessEvidenceReference.Attested(SomeHash, "customer-signoff:final-report-v3"), reasonCode: string.Empty, "approver-1",
            "Approver", CorrelationId.New(), Now);

        Assert.Equal(ReadinessControlStatus.Pass, attestation.Status);
    }

    [Fact]
    public void RehydrateThrowsWhenRecordHashIsTampered()
    {
        var attestation = MigrationCompletionCriterionAttestation.Create(
            Tenant, Project, new MigrationCompletionCriterionId("COMPLETION.ROLLBACK_DECOMMISSION_WINDOW_DEFINED"), 1, ReadinessControlStatus.Pass,
            ReadinessEvidenceReference.Attested(SomeHash, "rollback-window-definition:v1"), reasonCode: string.Empty, "approver-1",
            "Approver", CorrelationId.New(), Now);

        var tamperedHash = new Sha256Hash(new string('f', 64));
        var ex = Assert.Throws<MigrationCompletionIntegrityViolationException>(() => MigrationCompletionCriterionAttestation.Rehydrate(
            attestation.Tenant, attestation.Project, attestation.CriterionId, attestation.AttestationVersion, attestation.Status,
            attestation.Evidence, attestation.ReasonCode, attestation.SubmittedBy, attestation.SubmittedByRole, attestation.Correlation,
            attestation.SubmittedAtUtc, attestation.SchemaVersion, attestation.ContentFingerprint, tamperedHash));

        Assert.Contains("record_hash", ex.Message, StringComparison.Ordinal);
    }
}
