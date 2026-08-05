namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Violação de uma INVARIANTE de domínio da descoberta (fail-closed), detectada ANTES de qualquer
/// persistência — nunca se depende da mensagem textual do SQL (índice único 2601/2627). Carrega um
/// <see cref="Code"/> estruturado e estável para correlação/decisão.
/// </summary>
public sealed class EvDiscoveryInvariantException(string code, string message) : Exception(message)
{
    /// <summary>Código estruturado estável da invariante violada.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Invariantes de UNICIDADE da descoberta, alinhadas às constraints do banco
/// (<c>UQ_eveval_adapter</c> = único por <c>adapter_id</c>; <c>UQ_evcap_code</c> = único por
/// <c>capability_code</c>): no máximo uma <see cref="EvAdapterEvaluation"/> por <see cref="EvAdapterId"/>
/// numa execução, e no máximo uma <see cref="EvCapability"/> por <see cref="EvCapabilityCode"/> em cada
/// conjunto semântico (capability set e capacidades declaradas por avaliação). A validação é FAIL-CLOSED e
/// executada por TODOS os caminhos relevantes (fábrica de capability set, seleção de adapter, fingerprint,
/// serializer e store, antes de abrir a transação) — mesmo que um record público seja construído
/// diretamente, o hash/serializer/persistência recusam o objeto inválido.
/// </summary>
public static class EvDiscoveryInvariants
{
    /// <summary>Código: mais de uma avaliação para o mesmo <c>AdapterId</c> numa execução.</summary>
    public const string DuplicateAdapterId = "EV_INVARIANT_DUPLICATE_ADAPTER_ID";

    /// <summary>Código: mais de uma capacidade para o mesmo <c>CapabilityCode</c> num conjunto semântico.</summary>
    public const string DuplicateCapabilityCode = "EV_INVARIANT_DUPLICATE_CAPABILITY_CODE";

    /// <summary>Valida TODAS as invariantes de unicidade do resultado avaliado (fail-closed).</summary>
    public static void Validate(EvDiscoveryRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureUniqueCapabilities(result.CapabilitySet.Capabilities, "conjunto de capacidades");
        EnsureUniqueEvaluations(result.Selection.Evaluations);
    }

    /// <summary>Recusa capacidades com <see cref="EvCapabilityCode"/> repetido num conjunto semântico.</summary>
    public static void EnsureUniqueCapabilities(IReadOnlyList<EvCapability> capabilities, string context)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (!seen.Add(capability.CapabilityCode.Value))
            {
                throw new EvDiscoveryInvariantException(
                    DuplicateCapabilityCode,
                    $"Capacidade duplicada '{capability.CapabilityCode.Value}' em {context} (viola UQ_evcap_code; fail-closed).");
            }
        }
    }

    /// <summary>Recusa avaliações com <see cref="EvAdapterId"/> repetido e valida as capacidades de cada uma.</summary>
    public static void EnsureUniqueEvaluations(IReadOnlyList<EvAdapterEvaluation> evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evaluation in evaluations)
        {
            if (!seen.Add(evaluation.AdapterId.Value))
            {
                throw new EvDiscoveryInvariantException(
                    DuplicateAdapterId,
                    $"Avaliação de adapter duplicada '{evaluation.AdapterId.Value}' numa execução (viola UQ_eveval_adapter; fail-closed).");
            }

            EnsureUniqueCapabilities(evaluation.Capabilities, $"capacidades declaradas do adapter '{evaluation.AdapterId.Value}'");
        }
    }
}
