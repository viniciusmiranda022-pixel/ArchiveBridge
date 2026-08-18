namespace ArchiveBridge.ControlPlane.Presentation;

/// <summary>
/// Constrói a linha do tempo do ciclo de vida de uma onda (Draft → Validação → Aprovação → Frozen →
/// Execução) a partir do status atual. Puramente apresentacional — mapeia o status para o progresso visual.
/// Compartilhado pelas telas reais e pelo provedor de demonstração para manter uma única semântica.
/// </summary>
public static class WaveLifecycle
{
    private static readonly string[] Names = ["Draft", "Validação", "Aprovação", "Frozen", "Execução"];

    /// <summary>Etapas do ciclo de vida com o estado visual coerente com o status informado.</summary>
    public static IReadOnlyList<PipelineStage> Build(string status)
    {
        var reached = status switch
        {
            "Draft" => 1,
            "Validating" => 2,
            "Blocked" => 2,
            "ReadyForApproval" => 3,
            "Approved" => 4,
            "Frozen" => 4,
            "Completed" => 5,
            _ => 1,
        };

        var stages = new List<PipelineStage>();
        for (var i = 0; i < Names.Length; i++)
        {
            var number = i + 1;
            PipelineState state;
            string label;
            if (status == "Blocked" && number == 2)
            {
                state = PipelineState.Blocked;
                label = "Bloqueado";
            }
            else if (number < reached)
            {
                state = PipelineState.Done;
                label = "Concluído";
            }
            else if (number == reached)
            {
                state = status == "Completed" ? PipelineState.Done : PipelineState.Active;
                label = status == "Completed" ? "Concluído" : "Etapa atual";
            }
            else
            {
                state = PipelineState.Pending;
                label = "Pendente";
            }

            stages.Add(new PipelineStage(number, Names[i], state, label));
        }

        return stages;
    }
}
