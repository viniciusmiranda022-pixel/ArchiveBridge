namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Regra de reconciliação SEGURA e DOCUMENTADA para diretórios de staging órfãos deixados sob
/// <c>&lt;OutputRoot&gt;/.staging</c> por <see cref="LocalSinglePartExecutionWriter"/> (Slice 4B, Passo 3,
/// work order AB-4B-006 item 9). Um staging órfão nunca é confundido com uma parte concluída — a
/// canonicidade é decidida SOMENTE pelo bundle validado no caminho final
/// (<c>&lt;OutputRoot&gt;/&lt;tenant&gt;/&lt;projeto&gt;/&lt;plano&gt;/&lt;partKey&gt;</c>), nunca pela mera
/// presença de um diretório de staging. Esta classe é uma operação de MANUTENÇÃO explícita — nunca chamada
/// automaticamente no caminho de execução — pensada para ser invocada periodicamente por um operador/job de
/// limpeza (fora do escopo deste Passo: nenhum worker novo é registrado aqui).
/// <para>
/// Regra: um diretório sob <c>.staging</c> só é removido quando sua última escrita
/// (<see cref="Directory.GetLastWriteTimeUtc(string)"/>) é mais antiga que <c>olderThan</c> em relação a
/// <c>nowUtc</c>. Isso é seguro porque uma cópia legítima em andamento continua atualizando o arquivo (a
/// cada chunk gravado) — um limiar generoso (ex.: várias horas) nunca alcança um staging ativo, apenas um
/// órfão de verdade (crash ou falha sem cleanup). O diretório de staging RAIZ (<c>.staging</c> em si) nunca
/// é removido, apenas seus filhos imediatos.
/// </para>
/// </summary>
public static class TempStagingReconciler
{
    private const string StagingFolder = ".staging";

    /// <summary>
    /// Remove, sob a raiz de output configurada, os diretórios de staging órfãos mais antigos que
    /// <paramref name="olderThan"/> em relação a <paramref name="nowUtc"/>. Melhor esforço: falhas
    /// individuais de exclusão (arquivo em uso, permissão) são ignoradas silenciosamente — nunca interrompem
    /// a limpeza dos demais órfãos nem lançam para o chamador.
    /// </summary>
    /// <returns>Os caminhos efetivamente removidos.</returns>
    public static IReadOnlyList<string> CleanupOrphans(string outputRootPath, TimeSpan olderThan, DateTimeOffset nowUtc)
    {
        var stagingRoot = Path.Combine(Path.GetFullPath(outputRootPath), StagingFolder);
        if (!Directory.Exists(stagingRoot))
        {
            return [];
        }

        var removed = new List<string>();
        foreach (var candidate in Directory.EnumerateDirectories(stagingRoot))
        {
            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = Directory.GetLastWriteTimeUtc(candidate);
            }
            catch (IOException)
            {
                continue;
            }

            if (nowUtc.UtcDateTime - lastWriteUtc < olderThan)
            {
                continue; // Pode ainda estar em progresso — nunca remove antes do limiar de segurança.
            }

            try
            {
                Directory.Delete(candidate, recursive: true);
                removed.Add(candidate);
            }
            catch (IOException)
            {
                // Melhor esforço: outro processo pode estar usando o diretório neste instante.
            }
            catch (UnauthorizedAccessException)
            {
                // Idem.
            }
        }

        return removed;
    }
}
