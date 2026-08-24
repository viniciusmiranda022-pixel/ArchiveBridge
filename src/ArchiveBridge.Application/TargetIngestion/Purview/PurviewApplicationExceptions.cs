namespace ArchiveBridge.Application.TargetIngestion.Purview;

/// <summary>Onda inexistente ou fora do escopo — anti-IDOR, indistinguível de inexistente.</summary>
public sealed class PurviewWaveNotFoundException(string message) : Exception(message);
