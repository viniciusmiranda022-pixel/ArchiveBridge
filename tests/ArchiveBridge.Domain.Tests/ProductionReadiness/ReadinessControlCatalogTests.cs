using ArchiveBridge.Domain.ProductionReadiness;
using Xunit;

namespace ArchiveBridge.Domain.Tests.ProductionReadiness;

/// <summary>AB-I8-001 — <see cref="ReadinessControlCatalog"/>: identidade estável, cobertura fixa por grupo, e a classificação SystemDerived/Attested.</summary>
public sealed class ReadinessControlCatalogTests
{
    [Fact]
    public void AllControlsHaveUniqueStableIds()
    {
        var ids = ReadinessControlCatalog.AllControls.Select(definition => definition.Id.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(ReadinessGateGroup.Architecture, 5)]
    [InlineData(ReadinessGateGroup.Security, 7)]
    [InlineData(ReadinessGateGroup.Data, 5)]
    [InlineData(ReadinessGateGroup.Operations, 7)]
    [InlineData(ReadinessGateGroup.Microsoft365, 8)]
    public void EachGroupHasTheDocumentedControlCount(ReadinessGateGroup group, int expectedCount)
    {
        Assert.Equal(expectedCount, ReadinessControlCatalog.ControlsForGroup(group).Count);
    }

    [Fact]
    public void TotalControlCountMatchesTheSumOfAllGroups()
    {
        Assert.Equal(32, ReadinessControlCatalog.AllControls.Count);
    }

    [Fact]
    public void DefinitionThrowsForAnUnknownControl()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadinessControlCatalog.Definition(new ReadinessControlId("SEC.NONEXISTENT")));
    }

    [Fact]
    public void IsKnownIsFalseForAnUnknownControl()
    {
        Assert.False(ReadinessControlCatalog.IsKnown(new ReadinessControlId("SEC.NONEXISTENT")));
    }

    [Theory]
    [InlineData("ARCH.CAPABILITY_MATRIX_CURRENT")]
    [InlineData("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH")]
    [InlineData("SEC.SBOM_AND_SIGNATURES")]
    [InlineData("SEC.WDAC_DEFENDER_PATCHING")]
    [InlineData("SEC.INCIDENT_RESPONSE_EXERCISED")]
    [InlineData("DATA.HASHES_MANIFESTS_LINEAGE_WORM")]
    [InlineData("DATA.BACKUP_RESTORE_TESTED")]
    [InlineData("OPS.RTO_EXERCISED")]
    [InlineData("OPS.RPO_EXERCISED")]
    [InlineData("M365.TENANT_PRECHECK")]
    [InlineData("M365.AZCOPY_VERSION_HOMOLOGATED")]
    [InlineData("M365.MAPPING_VALIDATOR")]
    [InlineData("M365.TARGET_ROOT_POLICY")]
    [InlineData("M365.IMPORT_LIMITS_100GB_500ROWS")]
    public void TheFourteenSystemDerivedControlsAreClassifiedCorrectly(string controlId)
    {
        var definition = ReadinessControlCatalog.Definition(new ReadinessControlId(controlId));
        Assert.Equal(ReadinessControlEvidenceSource.SystemDerived, definition.EvidenceSource);
    }

    [Theory]
    [InlineData("ARCH.ADR_APPROVED")]
    [InlineData("ARCH.DATA_FLOW_DIAGRAMS_CURRENT")]
    [InlineData("ARCH.NO_PREVIEW_IN_GA_PATH")]
    [InlineData("ARCH.SERVICE_OWNERSHIP_ASSIGNED")]
    [InlineData("SEC.THREAT_MODEL_CLOSED")]
    [InlineData("SEC.SECRETS_SCAN_CLEAN")]
    [InlineData("SEC.CROSS_TENANT_TESTS")]
    [InlineData("DATA.PRIVACY_IMPACT_ASSESSMENT")]
    [InlineData("DATA.RETENTION_DELETION_DOCUMENTED")]
    [InlineData("DATA.CORPUS_FIDELITY_REPORT_APPROVED")]
    [InlineData("OPS.DASHBOARDS_ALERTS")]
    [InlineData("OPS.ONCALL_ESCALATION")]
    [InlineData("OPS.DLQ_RETRY_QUARANTINE_RUNBOOKS")]
    [InlineData("OPS.CAPACITY_FINOPS")]
    [InlineData("OPS.SUPPORT_PACKAGE_AUTOMATION")]
    [InlineData("M365.MINIMUM_ROLES")]
    [InlineData("M365.ARCHIVE_LICENSE_QUOTA")]
    [InlineData("M365.PORTAL_OPERATOR_TRAINED")]
    public void TheRemainingEighteenControlsStayAttested(string controlId)
    {
        // ARCHIVE_LICENSE_QUOTA em particular (AB-I8-002 blocker 2): nenhum store de evidência de
        // license/quota de archive existe hoje neste repositório — permanece Attested por ausência de fonte
        // canônica, não por omissão do catálogo.
        var definition = ReadinessControlCatalog.Definition(new ReadinessControlId(controlId));
        Assert.Equal(ReadinessControlEvidenceSource.Attested, definition.EvidenceSource);
    }

    [Fact]
    public void ExactlyFourteenControlsAreSystemDerived()
    {
        var systemDerivedCount = ReadinessControlCatalog.AllControls.Count(
            definition => definition.EvidenceSource == ReadinessControlEvidenceSource.SystemDerived);
        Assert.Equal(14, systemDerivedCount);
    }

    [Fact]
    public void EveryControlBelongsToTheGroupItIsFiledUnder()
    {
        foreach (var group in Enum.GetValues<ReadinessGateGroup>())
        {
            foreach (var definition in ReadinessControlCatalog.ControlsForGroup(group))
            {
                Assert.Equal(group, definition.Group);
            }
        }
    }
}
