namespace ArchiveBridge.Domain.Jobs;

/// <summary>Identidade de um worker que reivindica e executa Jobs (owner do lease).</summary>
public readonly record struct WorkerId
{
    /// <summary>Cria um <see cref="WorkerId"/> não vazio.</summary>
    /// <exception cref="ArgumentException">Quando o valor é nulo ou em branco.</exception>
    public WorkerId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("WorkerId não pode ser nulo ou vazio.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Identificador textual do worker.</summary>
    public string Value { get; }
}
