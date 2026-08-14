using System.Text;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// Passo 6A (unitário, sem SQL) — reforço do parser bounded e da validação estruturada: anti-amplificação,
/// ordem determinística dos problemas, teto de problemas persistidos e códigos estruturados.
/// </summary>
public sealed class Slice6MappingValidationUnitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly ContentCodePage CodePage = new(1252);

    private static MigrationWave ApprovedWave(params WaveEntry[] entries)
    {
        var wave = MigrationWave.Create(
            WaveId.New(),
            new TenantId(Guid.NewGuid()),
            new ProjectId(Guid.NewGuid()),
            new WaveName("W"),
            TargetRootFolder.ForWave("Proj", "Wave1"),
            new Sha256Hash("cfg"),
            new WaveSelection(entries.ToList()),
            Now);
        wave.StartValidation();
        wave.MarkReadyForApproval();
        wave.Approve("do", Now);
        return wave;
    }

    private static WaveEntry E(string pst, string mailbox) => new($"/src/{pst}", pst, new ArchiveRef(mailbox), 10, 1);

    private static string ValidCsv(MigrationWave wave) =>
        Encoding.UTF8.GetString(
            MappingCsvGenerator.Generate(wave, CodePage, MappingPolicy.Default, MappingVersion.Initial, "do", Now)
                .Document.GetBytes().Span);

    [Fact]
    public void ValidUploadCsvHasNoIssuesAndKnownRowCount()
    {
        var wave = ApprovedWave(E("a.pst", "u@contoso.com"), E("b.pst", "v@contoso.com"));
        var validation = MappingCsvValidator.ValidateUpload(ValidCsv(wave), wave, CodePage, MappingPolicy.Default, 1000);
        Assert.True(validation.IsValid);
        Assert.Empty(validation.Issues);
        Assert.Equal(2, validation.RowCount);
        Assert.False(validation.IssuesTruncated);
    }

    [Fact]
    public void RowsExceededIsBoundedNotAmplified()
    {
        // 5000 linhas ⇒ o parser materializa no máximo ~502 registros; a contagem de problemas não cresce
        // proporcionalmente à entrada hostil. Cada linha (Name vazio) gera 1 problema, além do rows-exceeded.
        var text = new StringBuilder();
        text.Append(MappingSchema.HeaderLine).Append("\r\n");
        for (var i = 0; i < 5000; i++)
        {
            text.Append("Exchange,,,,TRUE,,1252,,,\r\n");
        }

        var wave = ApprovedWave(E("a.pst", "u@contoso.com"));
        var validation = MappingCsvValidator.ValidateUpload(text.ToString(), wave, CodePage, MappingPolicy.Default, 1000);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, i => i.Code == MappingValidationIssueCode.RowsExceeded);
        Assert.True(validation.IssueCount < 600); // << 5000: sem amplificação
    }

    [Fact]
    public void IssueOrderIsDeterministicAcrossRuns()
    {
        var wave = ApprovedWave(E("a.pst", "u@contoso.com"));
        // CSV com múltiplos erros em linhas/colunas diferentes.
        var csv = MappingSchema.HeaderLine + "\r\n"
            + "=Bad,,,,notTrue,,,X,Y,Z\r\n"     // linha 2: fórmula(0)+workload(0)+archive(4)+sharepoint(7)+name(2)
            + "Exchange,/p,zzz.pst,m@c,TRUE,/f,1252,,,\r\n"; // linha 3: pst fora do manifesto

        var first = MappingCsvValidator.ValidateUpload(csv, wave, CodePage, MappingPolicy.Default, 1000).Issues;
        var second = MappingCsvValidator.ValidateUpload(csv, wave, CodePage, MappingPolicy.Default, 1000).Issues;

        Assert.Equal(
            first.Select(i => (i.Code, i.LineNumber, i.ColumnIndex)),
            second.Select(i => (i.Code, i.LineNumber, i.ColumnIndex)));
        // Ordenação: dentro da linha 2, coluna ASC ⇒ (col0) antes de (col4) antes de (col7).
        var line2 = first.Where(i => i.LineNumber == 2).ToList();
        Assert.True(line2.Select(i => i.ColumnIndex ?? -1).SequenceEqual(line2.Select(i => i.ColumnIndex ?? -1).OrderBy(x => x)));
    }

    [Fact]
    public void IssuesAreBoundedAndTruncatedUnderHostileInput()
    {
        var wave = ApprovedWave(E("a.pst", "u@contoso.com"));
        var text = new StringBuilder();
        text.Append(MappingSchema.HeaderLine).Append("\r\n");
        for (var i = 0; i < 400; i++)
        {
            text.Append("=Bad,,,,notTrue,,,X,Y,Z\r\n"); // ~5 problemas por linha
        }

        var validation = MappingCsvValidator.ValidateUpload(text.ToString(), wave, CodePage, MappingPolicy.Default, 1000);
        Assert.True(validation.IssueCount > 1000);       // total encontrado excede o teto
        Assert.True(validation.IssuesTruncated);
        Assert.Equal(1000, validation.Issues.Count);     // persistidos limitados ao teto
    }
}
