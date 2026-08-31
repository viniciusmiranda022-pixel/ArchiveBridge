namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Lançada quando um <see cref="CanaryPlan"/> JÁ PERSISTIDO falha a revalidação de integridade — a
/// persistência é fronteira NÃO CONFIÁVEL: um registro adulterado/corrompido nunca é reidratado, devolvido,
/// verificado como válido ou autorreparado silenciosamente (AB-I8-004 escopo obrigatório item 12).
/// </summary>
public sealed class CanaryIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public CanaryIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public CanaryIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public CanaryIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando se tenta autorizar um plano de canário sem um Production Readiness Review canônico e
/// vigente com desfecho <c>ReadyForCanary</c> (AB-I8-004 escopo obrigatório item 2: "Fail/Blocked/NotMeasured/
/// NotPerformed, evidence stale/tampered ou fingerprint divergente bloqueiam antes de qualquer efeito
/// externo"). Nenhum plano é criado quando esta exceção é lançada.
/// </summary>
public sealed class CanaryEntryGateBlockedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public CanaryEntryGateBlockedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public CanaryEntryGateBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public CanaryEntryGateBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando se tenta submeter evidência de um cenário por um caminho incompatível com a classificação
/// fixa do catálogo (AB-I8-004): atestação de operador para um cenário <c>SystemDerived</c> ou para o gate de
/// aprovação (<see cref="CanaryScenarioEvidenceSource.ApprovalDecision"/>), ou vice-versa — bloqueio
/// estrutural, mesmo princípio de
/// <see cref="ArchiveBridge.Domain.ProductionReadiness.ProductionReadinessAttestationNotAllowedException"/>.
/// </summary>
public sealed class CanaryScenarioNotAttestableException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public CanaryScenarioNotAttestableException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public CanaryScenarioNotAttestableException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public CanaryScenarioNotAttestableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando uma submissão de evidência de cenário referencia uma versão do plano que já não é a
/// vigente do (tenant, project) — drift do plano (novo Production Readiness Review, build/commit/digest ou
/// fingerprint de policy/capability diferentes) invalida a versão anterior para novas submissões (AB-I8-004
/// escopo obrigatório item 5: "mudança de commit SHA, artifact digest, policy version ou capability
/// fingerprint após início do canário invalida a promoção e exige novo canário"). Nenhum efeito é persistido
/// quando esta exceção é lançada.
/// </summary>
public sealed class CanaryPlanSupersededException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public CanaryPlanSupersededException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public CanaryPlanSupersededException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public CanaryPlanSupersededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando se tenta aprovar a primeira onda real (AB-I8-004 escopo obrigatório item 11) enquanto
/// qualquer outro cenário obrigatório do plano ainda não está <see cref="CanaryScenarioStatus.Pass"/> —
/// bloqueio estrutural, nenhuma aprovação é registrada quando esta exceção é lançada.
/// </summary>
public sealed class CanaryFirstWaveApprovalBlockedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public CanaryFirstWaveApprovalBlockedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public CanaryFirstWaveApprovalBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public CanaryFirstWaveApprovalBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
