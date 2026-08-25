using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;

/// <summary>
/// Uma linha de dados autorizada do mapping CSV do Purview Network Upload (Passo 4, AB-I5-012/AB-I5-013).
/// Ao contrário do mapping genérico do Slice 2 (<see cref="Mapping.MappingRow"/>, derivado diretamente de
/// <see cref="Waves.WaveEntry"/>), toda linha aqui é derivada exclusivamente de EVIDÊNCIA canônica de
/// custódia/upload já resolvida server-side (runbook §25.7/§25.8): <see cref="FilePath"/> e <see cref="Name"/>
/// vêm do prefixo/nome remoto REALMENTE usado pelo AzCopy (nunca de <c>WaveEntry.FilePath</c>/<c>PstName</c>,
/// que continuam sendo apenas planejamento); <see cref="Mailbox"/> vem da identidade resolvida do archive de
/// destino; e <see cref="IsArchive"/> só é <see langword="true"/> quando o precheck canônico de mailbox
/// comprovou o archive ativo/elegível para aquela identidade — nunca fixo, nunca inferido. Todos os campos
/// são validados na construção (fail-closed). A linha nunca carrega segredo, SAS, token ou conteúdo de e-mail.
/// </summary>
public sealed record PurviewMappingRow
{
    private const int MaxFilePathLength = 400;
    private const int MaxNameLength = 260;
    private const int MaxMailboxLength = 320;

    private PurviewMappingRow(string filePath, string name, string mailbox, bool isArchive, TargetRootFolder targetRootFolder)
    {
        FilePath = filePath;
        Name = name;
        Mailbox = mailbox;
        IsArchive = isArchive;
        TargetRootFolder = targetRootFolder;
    }

    /// <summary>Prefixo remoto canônico da onda (sem o container <c>ingestiondata</c>), exatamente como usado pelo AzCopy.</summary>
    public string FilePath { get; }

    /// <summary>Nome do arquivo PST remoto (<c>p_&lt;artifact&gt;_partNNN.pst</c>), exatamente como enviado pelo AzCopy.</summary>
    public string Name { get; }

    /// <summary>Mailbox de destino, resolvida server-side pela identidade aprovada da entrada da onda.</summary>
    public string Mailbox { get; }

    /// <summary>Verdadeiro somente quando o precheck canônico comprovou o archive ativo/elegível para esta identidade.</summary>
    public bool IsArchive { get; }

    /// <summary>Pasta raiz de destino aprovada da onda.</summary>
    public TargetRootFolder TargetRootFolder { get; }

    /// <summary>Cria uma linha autorizada a partir de evidência já resolvida server-side. Valida cada campo (defesa em profundidade).</summary>
    public static PurviewMappingRow Create(string filePath, string name, string mailbox, bool isArchive, TargetRootFolder targetRootFolder) =>
        new(
            ValidateFilePath(filePath),
            ValidateName(name),
            ValidateMailbox(mailbox),
            isArchive,
            targetRootFolder);

    private static string ValidateFilePath(string filePath)
    {
        var normalized = RequireNoControl(filePath, nameof(filePath), MaxFilePathLength);
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("FilePath não pode conter '..' (path traversal).", nameof(filePath));
        }

        if (normalized.Contains("ingestiondata", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "FilePath não pode referenciar o container reservado 'ingestiondata'.", nameof(filePath));
        }

        return normalized;
    }

    private static string ValidateName(string name)
    {
        var normalized = RequireNoControl(name, nameof(name), MaxNameLength);
        if (normalized.Contains('/', StringComparison.Ordinal) || normalized.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Name não pode conter separadores de caminho.", nameof(name));
        }

        if (!normalized.EndsWith(".pst", StringComparison.Ordinal))
        {
            throw new ArgumentException("Name deve terminar em '.pst' (case-sensitive).", nameof(name));
        }

        return normalized;
    }

    private static string ValidateMailbox(string mailbox)
    {
        var normalized = RequireNoControl(mailbox, nameof(mailbox), MaxMailboxLength);
        if (normalized.Contains(' ', StringComparison.Ordinal))
        {
            throw new ArgumentException("Mailbox não pode conter espaços (destino ambíguo).", nameof(mailbox));
        }

        return normalized;
    }

    private static string RequireNoControl(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} é obrigatório.", parameterName);
        }

        // Não faz trim: preserva o valor autorizado exatamente (case-sensitive).
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} excede {maxLength} caracteres.", parameterName);
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException($"{parameterName} contém caractere de controle.", parameterName);
            }
        }

        return value;
    }
}
