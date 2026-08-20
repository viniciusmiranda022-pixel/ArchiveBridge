using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>
/// Caminho relativo, server-side, de um PST dentro da raiz de custódia configurada (nunca uma raiz ou
/// caminho absoluto fornecidos pelo cliente). Só valida a FORMA do texto (sem travessia, sem caractere de
/// controle, tamanho limitado); a canonicalização/contenção real contra a raiz configurada e a rejeição de
/// symlink/reparse point acontecem em Infrastructure, imediatamente antes de abrir o arquivo (defesa em
/// profundidade — TOCTOU é mitigado na camada de I/O, não aqui).
/// </summary>
public readonly record struct PstRelativePath
{
    private const int MaxLength = 400;

    /// <summary>Valida e normaliza o caminho relativo.</summary>
    /// <exception cref="ArgumentException">Vazio, absoluto, com segmento de travessia ou caractere de controle.</exception>
    public PstRelativePath(string value)
    {
        var trimmed = TextValue.Require(value, nameof(value), MaxLength);

        // Verificação de "absoluto" deliberadamente NÃO usa Path.IsPathRooted: seu resultado depende da
        // plataforma em execução (em Linux, "C:\Windows\system32" não é considerado rooted, deixando um
        // caminho estilo drive-letter do Windows passar despercebido). A regra abaixo é a mesma em
        // qualquer SO: barra/contrabarra inicial (raiz OU UNC) já cai como segmento vazio abaixo; um
        // rótulo de unidade Windows ("C:", "D:"...) é rejeitado explicitamente aqui.
        var normalized = trimmed.Replace('\\', '/');
        if (normalized.Length >= 2 && normalized[1] == ':' && char.IsAsciiLetter(normalized[0]))
        {
            throw new ArgumentException("Caminho relativo de PST não pode ser absoluto (unidade Windows).", nameof(value));
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException(
                    "Caminho relativo de PST não pode conter segmento de travessia ('.' ou '..').", nameof(value));
            }

            if (segment.Length == 0)
            {
                throw new ArgumentException("Caminho relativo de PST não pode conter segmento vazio.", nameof(value));
            }
        }

        Value = trimmed;
    }

    /// <summary>Caminho relativo validado (ainda não canonicalizado contra a raiz).</summary>
    public string Value { get; }
}
