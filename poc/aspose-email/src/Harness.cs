// Infraestrutura comum do harness: métricas internas (memória/handles/
// progresso em CSV), resultados JSON, hash e PRNG determinístico.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AsposePoc;

/// <summary>Contexto de um caso: métricas + resultado + integridade.</summary>
public sealed class Harness : IDisposable
{
    private readonly string _case;
    private readonly string _resultsDir;
    private readonly StreamWriter _metrics;
    private readonly Timer _sampler;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _itemsDone;

    public double InputGb { get; set; }
    public bool OriginalHashUnchanged { get; set; } = true;
    public int RetryDuplicates { get; set; }
    public int Crashes { get; set; }
    public object? Detail { get; set; }

    public Harness(string caseName, string resultsDir, string metricsDir)
    {
        _case = caseName;
        _resultsDir = resultsDir;
        Directory.CreateDirectory(resultsDir);
        Directory.CreateDirectory(metricsDir);
        _metrics = new StreamWriter(
            Path.Combine(metricsDir, caseName + ".csv"), append: false,
            new UTF8Encoding(false));
        _metrics.WriteLine("utc,workingSetMb,privateMb,handles,itemsDone");
        _sampler = new Timer(_ => Sample(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    public void AddItems(long count) => Interlocked.Add(ref _itemsDone, count);

    private void Sample()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O},{p.WorkingSet64 / 1048576.0:F1}," +
            $"{p.PrivateMemorySize64 / 1048576.0:F1},{p.HandleCount}," +
            $"{Interlocked.Read(ref _itemsDone)}");
        lock (_metrics) _metrics.WriteLine(line);
    }

    /// <summary>Grava o resultado e retorna o exit code do caso.</summary>
    public int Complete(bool pass)
    {
        Sample();
        var result = new
        {
            @case = _case,
            ct = _case.Split('-')[0],
            status = pass ? "PASS" : "FAIL",
            inputGb = InputGb,
            elapsedHours = _clock.Elapsed.TotalHours,
            originalHashUnchanged = OriginalHashUnchanged,
            retryDuplicates = RetryDuplicates,
            crashes = Crashes,
            manualInterventions = 0,
            detail = Detail,
        };
        File.WriteAllText(
            Path.Combine(_resultsDir, _case + ".json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions
            { WriteIndented = true }));
        Console.WriteLine($"{_case}: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    public void Dispose()
    {
        _sampler.Dispose();
        lock (_metrics) _metrics.Dispose();
    }

    // ---------------------------------------------------------------------
    public static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static string Sha256Text(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
}

/// <summary>
/// PRNG determinístico próprio (xorshift64*): a estabilidade entre runtimes
/// é requisito do corpus, e System.Random não a garante entre versões.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(ulong seed) =>
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong NextULong()
    {
        var x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    public int Next(int maxExclusive) =>
        (int)(NextULong() % (ulong)maxExclusive);

    public void Fill(byte[] buffer)
    {
        for (var i = 0; i < buffer.Length; i += 8)
        {
            var v = NextULong();
            for (var j = 0; j < 8 && i + j < buffer.Length; j++)
                buffer[i + j] = (byte)(v >> (8 * j));
        }
    }
}
