using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Uma linha de dados autorizada do mapping, derivada de uma <see cref="WaveEntry"/> da onda aprovada.
/// Todos os campos são validados na construção (fail-closed): <c>Workload=Exchange</c> e
/// <c>IsArchive=TRUE</c> são invariantes do esquema; <c>FilePath</c> não contém traversal nem o
/// container reservado <c>ingestiondata</c>; <c>Name</c> termina em <c>.pst</c> (case-sensitive) e
/// não tem separadores de caminho; <c>Mailbox</c> é inequívoca; as colunas SharePoint ficam vazias.
/// A linha nunca carrega segredo, SAS, token ou conteúdo de e-mail.
/// </summary>
public sealed record MappingRow
{
    private const int MaxFilePathLength = 400;
    private const int MaxNameLength = 260;
    private const int MaxMailboxLength = 320;
    private const string ReservedContainer = "ingestiondata";

    private MappingRow(
        string filePath, string name, string mailbox, TargetRootFolder targetRootFolder, ContentCodePage contentCodePage)
    {
        FilePath = filePath;
        Name = name;
        Mailbox = mailbox;
        TargetRootFolder = targetRootFolder;
        ContentCodePage = contentCodePage;
    }

    /// <summary>Caminho de origem do PST (preservado com case; metadado, não conteúdo).</summary>
    public string FilePath { get; }

    /// <summary>Nome do arquivo PST (termina em <c>.pst</c>; case-sensitive; único na onda).</summary>
    public string Name { get; }

    /// <summary>Mailbox de destino cujo archive recebe a importação.</summary>
    public string Mailbox { get; }

    /// <summary>Pasta raiz de destino aprovada da onda.</summary>
    public TargetRootFolder TargetRootFolder { get; }

    /// <summary>Code page de conteúdo (conforme política).</summary>
    public ContentCodePage ContentCodePage { get; }

    /// <summary>
    /// Cria uma linha autorizada a partir de uma entrada da onda, da pasta de destino aprovada e do
    /// code page da política. Valida independentemente cada campo (defesa em profundidade).
    /// </summary>
    public static MappingRow Create(WaveEntry entry, TargetRootFolder targetRootFolder, ContentCodePage contentCodePage)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MappingRow(
            ValidateFilePath(entry.FilePath),
            ValidateName(entry.PstName),
            ValidateMailbox(entry.Archive.Mailbox),
            targetRootFolder,
            contentCodePage);
    }

    private static string ValidateFilePath(string filePath)
    {
        var normalized = RequireNoControl(filePath, nameof(filePath), MaxFilePathLength);
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("FilePath não pode conter '..' (path traversal).", nameof(filePath));
        }

        if (normalized.Contains(ReservedContainer, StringComparison.OrdinalIgnoreCase))
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
