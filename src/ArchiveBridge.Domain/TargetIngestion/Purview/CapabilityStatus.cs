namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Nível de suporte publicamente documentado pelo fornecedor para uma rota de capability (runbook §24:
/// "o retorno de DiscoverCapabilitiesAsync precisa distinguir GeneralAvailability, Preview, Contractual,
/// Unsupported e Unknown"). Este é um eixo DIFERENTE de
/// <see cref="ArchiveBridge.Domain.TargetIngestion.CapabilityState"/> (progressão de certificação interna
/// do ArchiveBridge para um adapter/rota, ADR-0007) — não conflar os dois. <see cref="Unknown"/> é o
/// default fail-closed (valor 0 do enum): nenhuma rota é tratada como suportada sem evidência explícita
/// ("honestidade comercial", mesma regra de <c>ConnectorSupportMatrix</c>/ADR-0013).
/// </summary>
public enum CapabilityStatus
{
    /// <summary>Rota não coberta por nenhuma evidência documentada — nunca tratada como suportada.</summary>
    Unknown,

    /// <summary>Rota identificada e explicitamente não suportada pela documentação do fornecedor.</summary>
    Unsupported,

    /// <summary>Disponível apenas sob contrato/acordo específico com o fornecedor (não GA pública).</summary>
    Contractual,

    /// <summary>Disponível em preview público — nunca promovida implicitamente a GA.</summary>
    Preview,

    /// <summary>Disponibilidade geral (GA) publicamente documentada pelo fornecedor.</summary>
    GeneralAvailability,
}
