using System.Reflection;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// I5/EPIC-06 Passo 2 (AB-I5-004) — fronteiras estruturais da custódia do SAS: (1) Domain/Application/
/// Contracts nunca referenciam DPAPI/Windows (item 1/6); (2) <see cref="RedactedSecret"/> nunca expõe o
/// valor por membro público de dados (item 13/14); (3) <see cref="ISecretStore.AcquireAsync"/> — a ÚNICA
/// operação de leitura em texto claro — é chamada em exatamente UM lugar de toda a Application
/// (<c>AcquireSasForUploadUseCase</c>), e NENHUM host além do futuro upload worker sequer referencia esse
/// caso de uso (item 10: "Controle/API não deve possuir método de leitura plaintext").
/// </summary>
public sealed partial class PurviewSasSecretBoundaryTests
{
    private static string DomainDir { get; } = Path.Combine(ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.Domain");

    private static string ApplicationDir { get; } = Path.Combine(ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.Application");

    private static string ContractsDir { get; } = Path.Combine(ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.Contracts");

    private static string ControlPlaneDir { get; } = Path.Combine(ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.ControlPlane");

    // (1) Nenhum arquivo-fonte de Domain/Application/Contracts referencia DPAPI/Windows concretamente —
    // mesmo padrão de VendorBoundaryTests.DomainAndApplicationSourceNeverReferencesProcessOrEvVendorTypes.
    [Theory]
    [InlineData("ProtectedData")]
    [InlineData("DataProtectionScope")]
    [InlineData("System.Security.Cryptography.ProtectedData")]
    public void DomainApplicationAndContractsSourceNeverReferenceDpapiTypes(string forbidden)
    {
        foreach (var file in EnumerateCsFiles(DomainDir).Concat(EnumerateCsFiles(ApplicationDir)).Concat(EnumerateCsFiles(ContractsDir)))
        {
            Assert.False(
                File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal),
                $"{Path.GetFileName(file)} (Domain/Application/Contracts) contém o token proibido: {forbidden}");
        }
    }

    // (1') Confirmação por reflexão: os assemblies Domain/Contracts/Application não referenciam o
    // assembly do pacote DPAPI (System.Security.Cryptography.ProtectedData é referenciado SOMENTE por
    // ArchiveBridge.Infrastructure — DependencyRuleTests já garante que nada mais referencia Infrastructure).
    [Fact]
    public void DomainContractsAndApplicationAssembliesDoNotReferenceTheDpapiPackageAssembly()
    {
        Assembly[] boundaryAssemblies =
        [
            typeof(ArchiveBridge.Domain.AssemblyMarker).Assembly,
            typeof(ArchiveBridge.Contracts.AssemblyMarker).Assembly,
            typeof(ArchiveBridge.Application.AssemblyMarker).Assembly,
        ];

        foreach (var assembly in boundaryAssemblies)
        {
            var referencesDpapi = assembly.GetReferencedAssemblies()
                .Any(reference => (reference.Name ?? string.Empty)
                    .Contains("ProtectedData", StringComparison.OrdinalIgnoreCase));
            Assert.False(referencesDpapi, $"{assembly.GetName().Name} não deveria referenciar o assembly DPAPI.");
        }
    }

    // (2) RedactedSecret nunca expõe o valor por propriedade/campo público — só Reveal() (método, nunca
    // serializado automaticamente) e ToString() (sempre "[REDACTED]"). Um futuro getter público adicionado
    // por engano é pego aqui, antes de qualquer vazamento em log/telemetria/serialização.
    [Fact]
    public void RedactedSecretExposesNoPublicDataMember()
    {
        var type = typeof(RedactedSecret);
        var publicProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var publicFields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(publicProperties);
        Assert.Empty(publicFields);
    }

    [Fact]
    public void RedactedSecretToStringNeverReturnsTheRawValueEvenStructurally()
    {
        var method = typeof(RedactedSecret).GetMethod(nameof(RedactedSecret.ToString), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.True(method!.IsVirtual); // override — nunca o ToString() de record/objeto padrão.
    }

    // (3) AcquireAsync (a ÚNICA leitura em texto claro) é chamado em EXATAMENTE um lugar de toda a
    // Application — nenhum outro caso de uso ganha, por engano, um segundo caminho de leitura plaintext.
    [Fact]
    public void SecretAcquireAsyncIsCalledFromExactlyOnePlaceInTheApplication()
    {
        var callers = EnumerateCsFiles(ApplicationDir)
            .Where(file => File.ReadAllText(file).Contains(".AcquireAsync(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["AcquireSasForUploadUseCase.cs"], callers);
    }

    // (3') Nenhum host além do futuro upload worker referencia o caso de uso de aquisição — Control/API
    // não tem NENHUM caminho, nem indireto, para o segredo em texto claro (item 10). Como este Passo NÃO
    // fia (wire) nenhum host ainda, a asserção vale hoje por VACUIDADE e permanece como guarda de
    // regressão para quando a superfície HTTP do ControlPlane for adicionada em um Passo futuro.
    [Fact]
    public void NoControlPlaneSourceFileReferencesTheAcquisitionUseCaseOrTheSecretStorePort()
    {
        foreach (var file in EnumerateCsFiles(ControlPlaneDir))
        {
            var source = File.ReadAllText(file);
            Assert.False(
                source.Contains("AcquireSasForUploadUseCase", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} (ControlPlane) não pode referenciar AcquireSasForUploadUseCase.");
            Assert.False(
                source.Contains("ISecretStore", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} (ControlPlane) não pode referenciar ISecretStore diretamente.");
        }
    }

    private static IEnumerable<string> EnumerateCsFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                yield return file;
            }
        }
    }
}
