namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>
/// Diagnóstico estrutural sanitizado de um PST (persistido como <c>TINYINT</c>). Só existe quando o
/// <see cref="PstInspectionOutcome"/> é <see cref="PstInspectionOutcome.Completed"/> — a engine sempre
/// termina num destes valores; nunca lança exceção não tratada nem reporta sucesso falso para um arquivo
/// ilegível, inválido ou truncado (§19/§22 do runbook de engine PST).
/// </summary>
public enum PstStructuralDiagnostic
{
    /// <summary>Assinatura/versão de cabeçalho reconhecidas; nenhuma inconsistência estrutural encontrada
    /// pelo mecanismo desta engine (que NÃO percorre a árvore NDB — ver decisão de adapter do Passo 1).</summary>
    Valid = 0,

    /// <summary>Arquivo menor que o cabeçalho mínimo do formato PST (truncado).</summary>
    TooSmall = 1,

    /// <summary>Assinatura mágica do arquivo (<c>dwMagic</c>) não corresponde a um PST.</summary>
    InvalidSignature = 2,

    /// <summary>Assinatura de cliente (<c>wMagicClient</c>) não corresponde ao esperado para PST.</summary>
    InvalidClientSignature = 3,

    /// <summary>Versão de cabeçalho (<c>wVer</c>) reconhecida como PST mas fora das variantes suportadas.</summary>
    UnsupportedVersion = 4,

    /// <summary>Falha de leitura sanitizada (permissão, I/O, arquivo removido entre custódia e abertura —
    /// TOCTOU) que impediu a inspeção; nunca vaza stack trace nem caminho real.</summary>
    ReadError = 5,
}
