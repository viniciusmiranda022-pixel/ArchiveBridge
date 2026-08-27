using System.Reflection;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// AB-I7-008 (I7 Hardening — Passo 4) — reforça, por reflexão sobre o assembly COMPILADO, os invariantes
/// estruturais que o STOP-THE-LINE do work order exige que sejam impossíveis "by construction", não apenas
/// por convenção de chamada: nenhum caso/membro público em <see cref="ArchiveBridge.Domain.Security"/> pode
/// representar um veredito de "Production Ready"/"GoLive"/pen-test concluído, e <see cref="PenTestReadinessStatus"/>
/// nunca ganha um terceiro valor por descuido em um PR futuro.
/// </summary>
public sealed class SecurityHardeningEvidenceArchitectureTests
{
    private static readonly string[] ForbiddenNameFragments =
    [
        "productionready",
        "golive",
        "go_live",
        "canaryapproved",
        "pentestcomplete",
        "pentestpass",
        "independentpentestpassed",
    ];

    private static Assembly DomainAssembly { get; } = typeof(WorkerHardeningControlRecord).Assembly;

    private static IEnumerable<Type> SecurityNamespaceTypes { get; } = DomainAssembly.GetTypes()
        .Where(type => type.Namespace == typeof(WorkerHardeningControlRecord).Namespace);

    [Fact]
    public void PenTestReadinessStatusHasExactlyTwoValuesAndNeverGainsAThirdCaseByAccident()
    {
        var values = Enum.GetValues<PenTestReadinessStatus>();

        Assert.Equal(2, values.Length);
        Assert.Equal([PenTestReadinessStatus.NotPerformed, PenTestReadinessStatus.Blocked], values);
    }

    [Fact]
    public void NoTypeInTheSecurityNamespaceHasANameThatImpliesAProjectWideProductionReadyVerdict()
    {
        var offendingTypes = SecurityNamespaceTypes
            .Where(type => ForbiddenNameFragments.Any(fragment =>
                type.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offendingTypes);
    }

    [Fact]
    public void NoPublicMemberInTheSecurityNamespaceHasANameThatImpliesAProjectWideProductionReadyVerdict()
    {
        var offendingMembers = SecurityNamespaceTypes
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(member => ForbiddenNameFragments.Any(fragment =>
                member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offendingMembers);
    }

    [Fact]
    public void NoEnumValueAnywhereInTheSecurityNamespaceIsNamedAfterAProductionReadyVerdict()
    {
        var offendingEnumValues = SecurityNamespaceTypes
            .Where(type => type.IsEnum)
            .SelectMany(Enum.GetNames)
            .Where(name => ForbiddenNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offendingEnumValues);
    }
}
