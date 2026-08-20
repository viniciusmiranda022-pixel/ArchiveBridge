using System.Runtime.CompilerServices;

// AB-4B-003: expõe apenas os tipos internos de seam necessários para os testes de
// ArchiveBridge.Integration.Tests provarem enforcement de PstStorageOptions.MaxSizeBytes sobre o stream
// efetivamente lido (ver IPstArtifactStreamFactory). Nenhum outro assembly de produção/teste precisa disto.
[assembly: InternalsVisibleTo("ArchiveBridge.Integration.Tests")]
