namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>
/// Desfecho TERMINAL de uma tentativa de upload (AB-I5-009). Cada tentativa produz exatamente um desfecho,
/// persistido de forma append-only (item 8) — a história de tentativas nunca é reescrita, apenas
/// estendida. Nenhum destes desfechos além de <see cref="Uploaded"/> produz evidência de sucesso do
/// transporte (item 11/13): exit code != 0, timeout, cancelamento, binário não homologado, fonte
/// adulterada/ausente e perda de fencing NUNCA produzem <see cref="Uploaded"/>.
/// </summary>
public enum PurviewUploadAttemptOutcome
{
    /// <summary>
    /// Sucesso do TRANSPORTE, comprovado (evidência sanitizada do AzCopy) — "UploadVerified" do work order.
    /// Distinto de importação/reconciliação Purview (item 13): nunca implica job de import criado/iniciado.
    /// </summary>
    Uploaded,

    /// <summary>
    /// Bloqueada ANTES de qualquer execução: revalidação física do PST fonte contra manifesto/hash/tamanho
    /// canônico já persistido falhou (ausente, adulterado ou stale) — item 12.
    /// </summary>
    SourceIntegrityFailed,

    /// <summary>Bloqueada ANTES de qualquer execução: versão/hash do binário AzCopy observado não corresponde ao catálogo homologado — item 5.</summary>
    BinaryMismatch,

    /// <summary>Bloqueada ANTES de qualquer execução: o SAS custodiado não pôde ser adquirido (claim/lease/expiry/escopo) — item 3.</summary>
    SasDenied,

    /// <summary>O processo AzCopy falhou (exit code != 0, timeout ou cancelamento) — item 11. Candidata a retry.</summary>
    ProcessFailed,
}

/// <summary>Classificação de retry do desfecho de uma tentativa.</summary>
public static class PurviewUploadAttemptOutcomes
{
    /// <summary>
    /// Verdadeiro quando o desfecho é definitivo (sem retry automático subsequente): sucesso, ou uma
    /// falha estrutural que uma nova tentativa idêntica não resolveria sozinha (fonte adulterada, binário
    /// não homologado). <see cref="PurviewUploadAttemptOutcome.SasDenied"/> e
    /// <see cref="PurviewUploadAttemptOutcome.ProcessFailed"/> são candidatos a retry (contenção de
    /// claim/lease e falha transitória de processo, respectivamente).
    /// </summary>
    public static bool IsTerminal(PurviewUploadAttemptOutcome outcome) =>
        outcome is PurviewUploadAttemptOutcome.Uploaded
            or PurviewUploadAttemptOutcome.SourceIntegrityFailed
            or PurviewUploadAttemptOutcome.BinaryMismatch;
}
