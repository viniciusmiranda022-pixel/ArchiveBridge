namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Desfecho de UM cenário obrigatório do canário de produção (AB-I8-004, escopo obrigatório item 4: "cada
/// cenário deve possuir estados equivalentes a Pending, Running, Pass, Fail, Blocked, NotPerformed quando
/// aplicável"). Mesmo padrão fail-closed de <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlStatus"/>:
/// o valor 0 do enum é sempre o estado mais conservador (nenhuma evidência produzida ainda) — ausência de
/// resultado externo, timeout ou resposta ambígua NUNCA vira <see cref="Pass"/>. Persistido como
/// <c>TINYINT</c> com o MESMO valor numérico desta enum.
/// </summary>
public enum CanaryScenarioStatus : byte
{
    /// <summary>Nenhuma tentativa de execução deste cenário foi registrada ainda — fail-closed default.</summary>
    Pending = 0,

    /// <summary>Uma execução do cenário está em andamento — NUNCA um desfecho terminal, nunca <see cref="Pass"/>.</summary>
    Running = 1,

    /// <summary>O cenário nunca foi de fato exercitado (ex.: nenhum drill compatível existe ainda).</summary>
    NotPerformed = 2,

    /// <summary>O cenário está explicitamente bloqueado por uma limitação/gate conhecida antes de qualquer efeito externo.</summary>
    Blocked = 3,

    /// <summary>O cenário foi exercitado e o resultado observado não satisfaz o requisito.</summary>
    Fail = 4,

    /// <summary>O cenário foi realmente verificado por evidência real e está conforme.</summary>
    Pass = 5,
}
