using System.Runtime.CompilerServices;

// AB-I8-001: expõe apenas os tipos internos de ArchiveBridge.Application.ProductionReadiness (resolvers de
// evidência e RBAC) necessários para ArchiveBridge.Application.Tests provar, sem SQL, os cenários fail-closed
// do work order (pen-test ausente, RTO/RPO não medidos, build digest alterado, etc.) diretamente sobre os
// resolvers puros. Nenhum outro assembly de produção/teste precisa disto.
[assembly: InternalsVisibleTo("ArchiveBridge.Application.Tests")]
