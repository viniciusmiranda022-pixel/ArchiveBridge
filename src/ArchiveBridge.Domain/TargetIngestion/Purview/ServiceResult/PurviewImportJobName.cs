using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Nome PLANEJADO do import job do Purview (runbook §25.9 item 67: "informar o nome gerado pelo produto,
/// apenas minúsculas, números, hífen e underscore"). Derivado EXCLUSIVAMENTE server-side a partir do
/// escopo tenant/projeto/onda já resolvidos e de uma sequência de tentativa monotônica (AB-I6-001 item 4)
/// — nunca de texto livre do operador, nunca do <c>provider_operation_id</c> observado depois da criação
/// humana do job. Hexadecimal + dígitos + hífen apenas: estruturalmente impossível de conter espaço,
/// maiúscula ou qualquer caractere fora do alfabeto exigido pelo portal.
/// </summary>
public readonly record struct PurviewImportJobName
{
    /// <summary>Tamanho máximo persistido (mesma ordem de grandeza histórica de <c>portal_job_name varchar(100)</c>).</summary>
    public const int MaxLength = 100;

    private const string Prefix = "ab-imp-";

    private PurviewImportJobName(string value) => Value = value;

    /// <summary>Nome planejado — somente <c>[a-z0-9-]</c>.</summary>
    public string Value { get; }

    /// <summary>
    /// Deriva o nome planejado, determinístico, a partir do escopo e da onda e de uma sequência de
    /// tentativa (1..N) — a MESMA tripla (tenant, projeto, onda) com a MESMA sequência produz sempre o
    /// MESMO nome; uma sequência diferente (nova tentativa, ex.: portal rejeitou o nome anterior) produz
    /// um nome diferente. Nunca deriva de nome de projeto/onda em texto livre.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attemptSequence"/> não é positivo.</exception>
    public static PurviewImportJobName Compute(TenantId tenant, ProjectId project, WaveId wave, int attemptSequence)
    {
        if (attemptSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptSequence), attemptSequence, "A sequência de tentativa começa em 1.");
        }

        var hash = DeterministicHash.Compute(
        [
            nameof(PurviewImportJobName),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            attemptSequence.ToString(CultureInfo.InvariantCulture),
        ]);

        // Os primeiros 16 caracteres hex (64 bits) bastam para unicidade prática dentro do escopo; o
        // hash inteiro já é hexadecimal minúsculo (DeterministicHash), portanto sempre satisfaz o
        // alfabeto exigido sem qualquer transformação adicional.
        var shortHash = hash.Value[..16];
        var value = $"{Prefix}{shortHash}-{attemptSequence.ToString(CultureInfo.InvariantCulture)}";
        return new PurviewImportJobName(value);
    }

    /// <summary>
    /// Reconstrói o nome a partir do valor JÁ PERSISTIDO (uso exclusivo da camada de persistência),
    /// revalidando o alfabeto/tamanho — a persistência é fronteira NÃO CONFIÁVEL; um valor adulterado
    /// nunca é reidratado como nome válido.
    /// </summary>
    /// <exception cref="PurviewImportJobIntegrityViolationException">Alfabeto/tamanho inválido.</exception>
    public static PurviewImportJobName FromPersistedValue(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            throw new PurviewImportJobIntegrityViolationException(
                "planned_job_name persistido é vazio ou excede o tamanho máximo (fail-closed).");
        }

        foreach (var character in value)
        {
            var isLowerAlnum = (character is >= 'a' and <= 'z') || (character is >= '0' and <= '9');
            if (!isLowerAlnum && character is not ('-' or '_'))
            {
                throw new PurviewImportJobIntegrityViolationException(
                    "planned_job_name persistido contém caractere fora do alfabeto do portal (minúsculas/dígitos/hífen/underscore) — fail-closed.");
            }
        }

        return new PurviewImportJobName(value);
    }
}
