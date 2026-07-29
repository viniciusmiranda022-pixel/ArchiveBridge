// Testes de integração batem em SQL Server real (I/O-bound). Serializá-los evita interferência
// entre tenants no reaper (que percorre todos os tenants) e mantém contagens determinísticas.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
