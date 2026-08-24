namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Status observado do Online Archive de uma mailbox (runbook §25.2, propriedade <c>ArchiveStatus</c> de
/// <c>Get-EXOMailbox</c>) — nunca conteúdo de mailbox. <see cref="Unknown"/> é o default fail-closed: um
/// precheck que não conseguiu determinar o status nunca é tratado como <see cref="Active"/>.
/// </summary>
public enum MailboxArchiveStatus
{
    /// <summary>Status não determinado pelo adapter — fail-closed, nunca tratado como <see cref="Active"/>.</summary>
    Unknown,

    /// <summary>Nenhum archive provisionado para a mailbox.</summary>
    None,

    /// <summary>Archive provisionado, porém desabilitado/inativo.</summary>
    Disabled,

    /// <summary>Archive provisionado e ativo — único status que autoriza import archive (work order item 7).</summary>
    Active,
}
