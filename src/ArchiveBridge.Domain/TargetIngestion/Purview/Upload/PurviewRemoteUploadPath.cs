using System.Globalization;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>
/// Prefixo remoto determinístico e OPACO da onda dentro do container <c>ingestiondata</c> (runbook §25.7,
/// AB-I5-009 item 4/acceptance criteria 11): <c>ingestiondata/&lt;tenantId&gt;-&lt;projectId&gt;-&lt;waveId&gt;/</c>.
/// Derivado EXCLUSIVAMENTE dos IDs opacos já resolvidos server-side (nunca de nome de projeto/onda
/// fornecido pelo caller, nunca de string humana livre) — hexadecimal puro, estruturalmente IMPOSSÍVEL de
/// conter <c>..</c>, barra, separador UNC ou qualquer caractere fora de <c>[0-9a-f-]</c>. Exclusivo por
/// (tenant, projeto, wave): dois escopos distintos NUNCA produzem o mesmo prefixo (mesma garantia de
/// unicidade de <c>TargetRootFolder.ForWave</c>, sem duplicar esse tipo — este é um caminho REMOTO de
/// transporte, não a pasta de import local do mapping CSV).
/// </summary>
public readonly record struct PurviewRemoteUploadPrefix
{
    /// <summary>Container fixo do Network Upload do Purview (runbook §25.5/§25.7) — nunca outro.</summary>
    public const string Container = "ingestiondata";

    private PurviewRemoteUploadPrefix(string waveSegment) => WaveSegment = waveSegment;

    /// <summary>
    /// Segmento OPACO exclusivo da onda, SEM o nome do container (ex.: <c>aaaa...-bbbb...-cccc...</c>) —
    /// é o que o adapter de upload precisa para compor o destino a partir da URL SAS já ancorada no
    /// container (a URL SAS já contém <c>/ingestiondata</c> como <c>AbsolutePath</c>; concatenar
    /// <see cref="Value"/> — que também começa com <c>ingestiondata/</c> — duplicaria o segmento).
    /// </summary>
    public string WaveSegment { get; }

    /// <summary>Caminho relativo canônico completo dentro do container, ex.: <c>ingestiondata/aaaa...-bbbb...-cccc...</c> (evidência/auditoria).</summary>
    public string Value => $"{Container}/{WaveSegment}";

    /// <summary>Constrói o prefixo canônico e exclusivo da onda.</summary>
    public static PurviewRemoteUploadPrefix ForWave(TenantId tenant, ProjectId project, WaveId wave) =>
        new($"{tenant.Value:N}-{project.Value:N}-{wave.Value:N}");

    /// <summary>Reconstrói o prefixo a partir do segmento JÁ PERSISTIDO (uso exclusivo da camada de persistência).</summary>
    public static PurviewRemoteUploadPrefix FromPersistedSegment(string waveSegment) => new(waveSegment);
}

/// <summary>
/// Nome de arquivo PST remoto, derivado de EVIDÊNCIA server-side já persistida (nunca de mailbox/UPN/caminho
/// de origem — item 4: "nomes de PST derivados de evidência server-side"): <c>p_&lt;artifactId&gt;_part&lt;NNN&gt;.pst</c>.
/// Único dentro do job (uma onda nunca repete o mesmo artefato+sequência) e estruturalmente seguro
/// (hexadecimal + dígitos + sublinhado apenas — sem traversal/separador possível).
/// </summary>
public readonly record struct PurviewRemotePstName
{
    private PurviewRemotePstName(string value) => Value = value;

    /// <summary>Nome de arquivo, sempre terminado em <c>.pst</c>.</summary>
    public string Value { get; }

    /// <summary>Constrói o nome determinístico a partir do artefato e da sequência da parte (1..N).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="partSequence"/> não é positivo.</exception>
    public static PurviewRemotePstName ForPart(PstProcessing.ArtifactId artifact, int partSequence)
    {
        if (partSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(partSequence), partSequence, "A sequência da parte começa em 1.");
        }

        return new PurviewRemotePstName(
            $"p_{artifact.Value:N}_part{partSequence.ToString("D3", CultureInfo.InvariantCulture)}.pst");
    }
}
