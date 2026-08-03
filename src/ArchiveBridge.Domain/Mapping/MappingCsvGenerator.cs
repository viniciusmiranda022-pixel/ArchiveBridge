using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Serviço de domínio que gera o CSV de mapping a partir de uma onda aprovada (seleção congelada).
/// Aplica, de forma fail-closed: onda aprovada/congelada; code page conforme política; limite de
/// 500 linhas de dados; nomes de PST únicos; e serialização segura contra injeção de fórmula. A
/// geração é determinística e liga o artefato à fonte por hashes (configuração + seleção).
/// </summary>
public static class MappingCsvGenerator
{
    /// <summary>
    /// Versão do gerador. Faz parte do <see cref="MappingGenerationFingerprint"/>: uma mudança na
    /// lógica de geração invalida a idempotência de versões produzidas pela lógica anterior.
    /// </summary>
    public const int GeneratorVersion = 1;

    /// <summary>Gera o documento CSV e a evidência de versão para a onda aprovada.</summary>
    public static MappingGenerationResult Generate(
        MigrationWave wave,
        ContentCodePage contentCodePage,
        MappingPolicy policy,
        MappingVersion version,
        string generatedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(wave);
        ArgumentNullException.ThrowIfNull(policy);
        var author = RequireGeneratedBy(generatedBy);
        EnsureWaveFrozenForMapping(wave);
        policy.EnsureAllowed(contentCodePage);

        var entries = wave.Selection.Entries;
        if (entries.Count == 0)
        {
            throw new MappingGenerationException("A onda não tem entradas; nada a mapear.");
        }

        if (entries.Count > MappingSchema.MaxDataRows)
        {
            throw new MappingGenerationException(
                $"O mapping excede o limite de {MappingSchema.MaxDataRows} linhas de dados.");
        }

        var rows = new List<MappingRow>(entries.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var row = MappingRow.Create(entry, wave.TargetRootFolder, contentCodePage);
            if (!names.Add(row.Name))
            {
                throw new MappingGenerationException("Nome de PST duplicado no mapping (fail-closed).");
            }

            rows.Add(row);
        }

        var content = MappingCsvSerializer.Serialize(rows);
        var document = new MappingDocument(content, rows.Count);
        var fingerprint = MappingGenerationFingerprint.Compute(
            wave.ConfigurationHash,
            wave.SelectionHash,
            wave.TargetRootFolder,
            contentCodePage,
            MappingSchema.Version,
            GeneratorVersion,
            policy.Version);
        var evidence = new MappingCsvVersion(
            version,
            wave.Project,
            wave.Id,
            wave.ConfigurationHash,
            wave.SelectionHash,
            document.ContentSha256,
            document.RowCount,
            MappingValidationOutcome.Passed,
            author,
            now,
            MappingVersionStatus.Usable,
            fingerprint,
            ArtifactPath: string.Empty);

        return new MappingGenerationResult(document, evidence, rows);
    }

    private static void EnsureWaveFrozenForMapping(MigrationWave wave)
    {
        // O mapping só é gerado a partir de uma seleção imutável (aprovada/congelada), nunca de Draft.
        if (wave.Status is not (WaveStatus.Approved or WaveStatus.Frozen))
        {
            throw new MappingGenerationException(
                $"O mapping só pode ser gerado de uma onda aprovada/congelada; estado atual: {wave.Status}.");
        }
    }

    private static string RequireGeneratedBy(string generatedBy)
    {
        if (string.IsNullOrWhiteSpace(generatedBy))
        {
            throw new ArgumentException("generatedBy é obrigatório.", nameof(generatedBy));
        }

        return generatedBy.Trim();
    }
}
