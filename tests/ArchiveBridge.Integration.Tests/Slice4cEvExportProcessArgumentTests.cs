using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Infrastructure.EnterpriseVault.Export;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4C, Passo 2 (AB-4C-005) — provas de que a construção do comando <c>Export-EVArchive</c> é IMUNE a
/// command injection (item 7) e que o diretório de saída resolvido é SEMPRE contido na raiz autorizada do
/// connector (item 9, §15.2): traversal, UNC não allowlisted, symlink/junction/reparse point. Nenhum destes
/// testes inicia um processo real nem toca um Enterprise Vault — apenas a construção de
/// <see cref="System.Diagnostics.ProcessStartInfo"/> e a resolução de caminho.
/// </summary>
public sealed class Slice4cEvExportProcessArgumentTests
{
    // Corpus de metacaracteres/técnicas clássicas de command injection para shell/PowerShell.
    public static TheoryData<string> InjectionCorpus() =>
        new()
        {
            "arch1; Remove-Item C:\\ -Recurse -Force",
            "arch1 && calc.exe",
            "arch1 | Out-File evil.txt",
            "arch1`nInvoke-Expression 'malicious'",
            "arch1\r\nStart-Process cmd",
            "arch1$(Remove-Item C:\\)",
            "arch1${env:PATH}",
            "arch1\"; Invoke-Expression 'x'; \"",
            "arch1' ; DROP TABLE jobs; --",
            "arch1`whoami`",
            "arch1 & net user hacker P@ssw0rd /add",
        };

    [Theory]
    [MemberData(nameof(InjectionCorpus))]
    public void MaliciousArchiveIdNeverEntersTheProcessArgumentListOrScriptText(string maliciousArchiveId)
    {
        // Defesa em profundidade DUPLA: um valor com caractere de CONTROLE (CR/LF) já é recusado na
        // FRONTEIRA do Domain (TextValue.Require, fail-closed ANTES de qualquer processo) — prova tão
        // válida de segurança quanto a segunda camada abaixo, que cobre os demais metacaracteres que o
        // Domain aceita como texto (';', aspas, backtick, pipe, '$(...)', '${...}') mas que NUNCA chegam a
        // influenciar o argv/script do processo.
        if (maliciousArchiveId.Any(char.IsControl))
        {
            Assert.Throws<ArgumentException>(
                () => EvExportCommandArguments.Create(maliciousArchiveId, "aa/bb/cc/dd/ee", ExportRequestPolicy.Default));
            return;
        }

        var arguments = EvExportCommandArguments.Create(maliciousArchiveId, "aa/bb/cc/dd/ee", ExportRequestPolicy.Default);
        var request = new EvExportProcessRequest(
            arguments, new EvExportOutputLocation(Path.GetTempPath(), "aa/bb/cc/dd/ee"), "1.0.0", TimeSpan.FromMinutes(1), 1024);

        var startInfo = EvExportProcessArgumentBuilder.Build(request, "powershell.exe", Path.GetTempPath(), Path.GetTempPath());

        // (1) O script é SEMPRE o texto FIXO (constante) — nunca contém o valor malicioso.
        var scriptArgument = Assert.Single(startInfo.ArgumentList, arg => arg.Contains("Export-EVArchive", StringComparison.Ordinal));
        Assert.Equal(EvExportProcessScript.Script, scriptArgument);
        Assert.DoesNotContain(maliciousArchiveId, scriptArgument, StringComparison.Ordinal);

        // (2) NENHUM item de ArgumentList (argv do processo) contém o valor malicioso.
        Assert.DoesNotContain(startInfo.ArgumentList, arg => arg.Contains(maliciousArchiveId, StringComparison.Ordinal));

        // (3) O valor entra EXCLUSIVAMENTE como conteúdo de uma variável de ambiente — verbatim, nunca
        // reinterpretado/escapado — provando que a única via de entrada é dado, nunca código.
        Assert.Equal(maliciousArchiveId, startInfo.Environment[EvExportProcessArgumentBuilder.ArgumentEnvironmentPrefix + "ArchiveId"]);
    }

    [Fact]
    public void NumericArgumentsAreAlwaysWellFormedIntegersInEnvironment()
    {
        var arguments = EvExportCommandArguments.Create("arch-1", "aa/bb", ExportRequestPolicy.Create(20000, 12));
        var request = new EvExportProcessRequest(
            arguments, new EvExportOutputLocation(Path.GetTempPath(), "aa/bb"), "1.0.0", TimeSpan.FromMinutes(1), 1024);

        var startInfo = EvExportProcessArgumentBuilder.Build(request, "powershell.exe", Path.GetTempPath(), Path.GetTempPath());

        Assert.Equal("12", startInfo.Environment[EvExportProcessArgumentBuilder.ArgumentEnvironmentPrefix + "MaxThreads"]);
        Assert.Equal("20000", startInfo.Environment[EvExportProcessArgumentBuilder.ArgumentEnvironmentPrefix + "MaxPstSizeMb"]);
    }

    [Fact]
    public void ArgumentListNeverContainsAConcatenatedCommandLine()
    {
        // Defesa em profundidade: o array de argumentos nunca é uma ÚNICA string longa concatenada (o que
        // indicaria montagem via string de shell) — cada elemento é um token curto e discreto, exceto o
        // próprio script (constante fixa, nunca dado).
        var arguments = EvExportCommandArguments.Create("arch-1", "aa/bb", ExportRequestPolicy.Default);
        var request = new EvExportProcessRequest(
            arguments, new EvExportOutputLocation(Path.GetTempPath(), "aa/bb"), "1.0.0", TimeSpan.FromMinutes(1), 1024);

        var startInfo = EvExportProcessArgumentBuilder.Build(request, "powershell.exe", Path.GetTempPath(), Path.GetTempPath());

        Assert.Equal(["-NoProfile", "-NonInteractive", "-NoLogo", "-OutputFormat", "Text", "-Command", EvExportProcessScript.Script],
            startInfo.ArgumentList);
    }

    // ---- Containment do diretório de saída (item 9, §15.2) ---------------------------------------------

    [Fact]
    public void RelativeTokenEscapingWithDotDotIsRejected()
    {
        using var root = new TempRoot();
        var location = new EvExportOutputLocation(root.Path, "../../etc/passwd");

        Assert.Throws<EvExportOutputContainmentException>(() => EvExportOutputPaths.ResolvePhysicalDirectory(location));
    }

    [Fact]
    public void RelativeTokenEscapingWithAbsolutePathIsRejected()
    {
        using var root = new TempRoot();
        var escapeTarget = OperatingSystem.IsWindows() ? "C:\\Windows\\System32" : "/etc";
        var location = new EvExportOutputLocation(root.Path, escapeTarget);

        Assert.Throws<EvExportOutputContainmentException>(() => EvExportOutputPaths.ResolvePhysicalDirectory(location));
    }

    [Fact]
    public void ASiblingDirectoryWithASimilarPrefixIsNeverTreatedAsContained()
    {
        using var root = new TempRoot();
        var evilSibling = root.Path + "_evil";
        Directory.CreateDirectory(evilSibling);
        try
        {
            // "root_evil" NÃO é "root" — uma checagem StartsWith ingênua aceitaria isto erroneamente.
            var location = new EvExportOutputLocation(root.Path, Path.Combine("..", Path.GetFileName(evilSibling)));
            Assert.Throws<EvExportOutputContainmentException>(() => EvExportOutputPaths.ResolvePhysicalDirectory(location));
        }
        finally
        {
            Directory.Delete(evilSibling, recursive: true);
        }
    }

    [Fact]
    public void AWellFormedRelativeTokenResolvesInsideTheRoot()
    {
        using var root = new TempRoot();
        var location = new EvExportOutputLocation(root.Path, "aa/bb/cc/dd/ee");

        var resolved = EvExportOutputPaths.ResolvePhysicalDirectory(location);

        Assert.StartsWith(Path.GetFullPath(root.Path), resolved, StringComparison.Ordinal);
    }

    // Symlinks podem exigir privilégio elevado em alguns hosts de CI (especialmente Windows sem modo
    // desenvolvedor); quando o host não permite criar um, o teste retorna cedo (soft-skip) em vez de falhar
    // por uma limitação de ambiente alheia à lógica sob teste — CanCreateSymlinks() prova a causa.
    [Fact]
    public void ASymlinkInTheChainIsRejected()
    {
        if (!CanCreateSymlinks())
        {
            return; // ambiente sandbox sem permissão para criar symlink — não é um falso positivo do teste.
        }

        using var root = new TempRoot();
        var real = Path.Combine(root.Path, "real");
        Directory.CreateDirectory(real);
        var linkPath = Path.Combine(root.Path, "link");
        Directory.CreateSymbolicLink(linkPath, real);

        var location = new EvExportOutputLocation(root.Path, Path.Combine("link", "aa"));

        Assert.Throws<EvExportOutputContainmentException>(() => EvExportOutputPaths.ResolvePhysicalDirectory(location));
    }

    private static bool CanCreateSymlinks()
    {
        try
        {
            var tempDir = Directory.CreateTempSubdirectory();
            try
            {
                var target = Path.Combine(tempDir.FullName, "target");
                Directory.CreateDirectory(target);
                Directory.CreateSymbolicLink(Path.Combine(tempDir.FullName, "link"), target);
                return true;
            }
            finally
            {
                tempDir.Delete(recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- Raiz UNC não allowlisted (item 9, §15.2) -------------------------------------------------------

    [Fact]
    public void AnUncRootNotOnTheAllowlistFailsClosedAtStartup()
    {
        var options = new EvExportOutputRootOptions(@"\\fileserver\export", AllowedUncRoots: []);
        Assert.Throws<EvExportOutputContainmentException>(options.Validate);
    }

    [Fact]
    public void AnAllowlistedUncRootValidatesCleanly()
    {
        var options = new EvExportOutputRootOptions(@"\\fileserver\export", AllowedUncRoots: [@"\\fileserver\export"]);
        options.Validate(); // não lança
    }

    [Fact]
    public void ALocalRootNeverRequiresTheUncAllowlist()
    {
        using var root = new TempRoot();
        var options = new EvExportOutputRootOptions(root.Path, AllowedUncRoots: []);
        options.Validate(); // não lança — só caminhos UNC exigem allowlist
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("ab-ev-export-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
