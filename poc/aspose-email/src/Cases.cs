// Casos CT-1 a CT-5 do plano de PoC (docs/adr/0004-poc-plan.md, seção 4).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Aspose.Email.Storage.Pst;

namespace AsposePoc;

public static class Cases
{
    // =================================================================
    // CT-1 — Abertura e inspeção sem extração (§19)
    // =================================================================
    public static int RunCt1(Dictionary<string, string> opts)
    {
        var pstPath = opts["pst"];
        var caseName = "ct1-" + Path.GetFileNameWithoutExtension(pstPath);
        using var h = new Harness(caseName, opts["results"], opts["metrics"]);
        h.InputGb = new FileInfo(pstPath).Length / (double)(1L << 30);

        var hashBefore = Harness.Sha256File(pstPath);
        var inspection = Inspect(pstPath, h);
        h.OriginalHashUnchanged = Harness.Sha256File(pstPath) == hashBefore;

        var expected = LoadExpected(pstPath);
        var pass = h.OriginalHashUnchanged && inspection.Opened;
        if (expected is not null && inspection.Opened)
        {
            pass &= inspection.TotalItems == expected.Value.TotalItems;
        }
        h.Detail = new
        {
            inspection.Opened,
            inspection.Format,
            inspection.TotalItems,
            inspection.FolderCount,
            expectedItems = expected?.TotalItems,
        };
        return h.Complete(pass);
    }

    // =================================================================
    // CT-2 — Split por tamanho com hard limit e reinspeção (§20.3)
    // =================================================================
    public static int RunCt2(Dictionary<string, string> opts)
    {
        var pstPath = opts["pst"];
        var caseName = "ct2-" + Path.GetFileNameWithoutExtension(pstPath);
        using var h = new Harness(caseName, opts["results"], opts["metrics"]);
        h.InputGb = new FileInfo(pstPath).Length / (double)(1L << 30);

        const long targetBytes = 18L * 1024 * 1024 * 1024; // 18 GiB
        const long hardBytes = 20_000_000_000;             // 20 GB (limite duro)

        var hashBefore = Harness.Sha256File(pstPath);
        var runDir = Path.Combine(opts["workdir"], caseName, "run1");
        var parts1 = SplitOnce(pstPath, runDir, targetBytes, h);

        // Retry integral: segunda execução limpa; mesma contagem lógica,
        // nenhuma duplicação no conjunto aprovado (run1).
        var retryDir = Path.Combine(opts["workdir"], caseName, "run2");
        var parts2 = SplitOnce(pstPath, retryDir, targetBytes, h);

        h.OriginalHashUnchanged = Harness.Sha256File(pstPath) == hashBefore;
        var oversize = parts1.Where(p => p.SizeBytes > hardBytes).ToList();
        var count1 = parts1.Sum(p => p.Items);
        var count2 = parts2.Sum(p => p.Items);
        h.RetryDuplicates = count2 == count1 ? 0 : Math.Abs(count2 - count1);

        var expected = LoadExpected(pstPath);
        var pass = h.OriginalHashUnchanged
                   && oversize.Count == 0
                   && parts1.All(p => p.Reopened)
                   && h.RetryDuplicates == 0
                   && (expected is null || count1 == expected.Value.TotalItems);
        h.Detail = new
        {
            parts = parts1.Count,
            itemsRun1 = count1,
            itemsRun2 = count2,
            expectedItems = expected?.TotalItems,
            oversizeParts = oversize.Select(p => new { p.Path, p.SizeBytes }),
        };
        return h.Complete(pass);
    }

    private sealed record PartResult(string Path, long SizeBytes, long Items,
        bool Reopened);

    private static List<PartResult> SplitOnce(string pstPath, string outDir,
        long targetBytes, Harness h)
    {
        Directory.CreateDirectory(outDir);
        using (var pst = PersonalStorage.FromFile(pstPath, writable: false))
        {
            // Progresso apenas com contadores/nomes sanitizados (runbook §20.3).
            pst.StorageProcessing += (_, _) => h.AddItems(0);
            pst.SplitInto(targetBytes, "part", outDir);
        }
        var parts = new List<PartResult>();
        foreach (var file in Directory.EnumerateFiles(outDir, "*.pst")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var size = new FileInfo(file).Length;
            var inspection = Inspect(file, h);
            parts.Add(new PartResult(file, size, inspection.TotalItems,
                inspection.Opened));
        }
        return parts;
    }

    // =================================================================
    // CT-3 — Criação e partição semântica mínima (§20.4, §23)
    // =================================================================
    public static int RunCt3(Dictionary<string, string> opts)
    {
        var pstPath = opts["pst"];
        var caseName = "ct3-" + Path.GetFileNameWithoutExtension(pstPath);
        using var h = new Harness(caseName, opts["results"], opts["metrics"]);
        h.InputGb = new FileInfo(pstPath).Length / (double)(1L << 30);

        var hashBefore = Harness.Sha256File(pstPath);
        var outPst = Path.Combine(opts["workdir"], caseName + "-semantic.pst");
        Directory.CreateDirectory(Path.GetDirectoryName(outPst)!);
        if (File.Exists(outPst)) File.Delete(outPst);

        long copied = 0;
        var sourceFingerprints = new List<string>();
        using (var source = PersonalStorage.FromFile(pstPath, writable: false))
        using (var target = PersonalStorage.Create(outPst,
                   FileFormatVersion.Unicode))
        {
            // Um único writer aberto por vez sobre o destino (§20.4).
            foreach (var (path, folder) in EnumerateFolders(source.RootFolder))
            {
                var destFolder = EnsurePath(target, path);
                foreach (var info in folder.EnumerateMessages())
                {
                    var msg = source.ExtractMessage(info);
                    sourceFingerprints.Add(Harness.Sha256Text(
                        $"{path}|{msg.Subject}|{msg.DeliveryTime:O}"));
                    destFolder.AddMessage(msg);
                    copied++;
                    h.AddItems(1);
                }
            }
        } // fechar/flush antes de reinspecionar (§20.4 itens 50-51)

        // Reabrir e comparar contagem + fingerprints (§20.4 item 52).
        long reread = 0;
        var targetFingerprints = new HashSet<string>(StringComparer.Ordinal);
        using (var reopened = PersonalStorage.FromFile(outPst, writable: false))
        {
            foreach (var (path, folder) in EnumerateFolders(reopened.RootFolder))
            {
                foreach (var info in folder.EnumerateMessages())
                {
                    var msg = reopened.ExtractMessage(info);
                    targetFingerprints.Add(Harness.Sha256Text(
                        $"{path}|{msg.Subject}|{msg.DeliveryTime:O}"));
                    reread++;
                }
            }
        }

        h.OriginalHashUnchanged = Harness.Sha256File(pstPath) == hashBefore;
        var sample = sourceFingerprints.Where((_, i) => i % 97 == 0).ToList();
        var missing = sample.Count(fp => !targetFingerprints.Contains(fp));
        var pass = h.OriginalHashUnchanged && copied == reread && missing == 0;
        h.Detail = new { copied, reread, sampled = sample.Count, missing };
        return h.Complete(pass);
    }

    // =================================================================
    // CT-4 — Determinismo do plano (§20.2)
    // =================================================================
    public static int RunCt4(Dictionary<string, string> opts)
    {
        var pstPath = opts["pst"];
        var caseName = "ct4-" + Path.GetFileNameWithoutExtension(pstPath);
        using var h = new Harness(caseName, opts["results"], opts["metrics"]);
        h.InputGb = new FileInfo(pstPath).Length / (double)(1L << 30);

        var hashBefore = Harness.Sha256File(pstPath);
        var hash1 = AssignmentHash(pstPath, h);
        var hash2 = AssignmentHash(pstPath, h);
        h.OriginalHashUnchanged = Harness.Sha256File(pstPath) == hashBefore;

        var pass = h.OriginalHashUnchanged && hash1 == hash2;
        h.Detail = new { assignmentHash1 = hash1, assignmentHash2 = hash2 };
        return h.Complete(pass);
    }

    private static string AssignmentHash(string pstPath, Harness h)
    {
        // Ordem estável: folderPathNormalized + receivedUtc + fingerprint
        // (§20.1); o hash da sequência ordenada representa a associação
        // lógica do plano (§20.2).
        var keys = new List<string>();
        using var pst = PersonalStorage.FromFile(pstPath, writable: false);
        foreach (var (path, folder) in EnumerateFolders(pst.RootFolder))
        {
            var normalized = path.ToUpperInvariant();
            foreach (var info in folder.EnumerateMessages())
            {
                var msg = pst.ExtractMessage(info);
                keys.Add($"{normalized}|{msg.DeliveryTime:O}|" +
                         Harness.Sha256Text(msg.Subject ?? ""));
                h.AddItems(1);
            }
        }
        keys.Sort(StringComparer.Ordinal);
        return Harness.Sha256Text(string.Join("\n", keys));
    }

    // =================================================================
    // CT-5 — Anomalias: truncado, corrompido, senha (§22)
    // =================================================================
    public static int RunCt5(Dictionary<string, string> opts)
    {
        var corpusDir = opts["corpus"];
        using var h = new Harness("ct5-anomalies", opts["results"],
            opts["metrics"]);
        var outcomes = new List<object>();
        var pass = true;

        foreach (var anomaly in Directory.EnumerateFiles(corpusDir, "a-*.pst")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var hashBefore = File.ReadAllText(anomaly + ".sha256").Trim();
            bool detected;
            string outcome;
            try
            {
                var inspection = Inspect(anomaly, h);
                // Abrir "com sucesso" um arquivo adulterado sem reportar
                // problema algum é falha de detecção.
                detected = !inspection.Opened || inspection.Unreadable > 0;
                outcome = inspection.Opened
                    ? $"abriu com {inspection.Unreadable} item(ns) ilegível(is)"
                    : "recusou abertura (erro estruturado)";
            }
            catch (Exception ex)
            {
                // Exceção estruturada da engine é detecção aceitável;
                // o harness permanece vivo (sem crash do processo).
                detected = true;
                outcome = $"exceção estruturada: {ex.GetType().Name}";
            }
            var intact = Harness.Sha256File(anomaly) == hashBefore;
            if (!intact) h.OriginalHashUnchanged = false;
            pass &= detected && intact;
            outcomes.Add(new
            {
                file = Path.GetFileName(anomaly),
                detected,
                intact,
                outcome,
            });
        }
        h.Detail = new
        {
            outcomes,
            note = "caso senha: preparar manualmente se a API suportar; " +
                   "registrar ausência no relatório",
        };
        return h.Complete(pass && outcomes.Count > 0);
    }

    // =================================================================
    // Inspeção compartilhada (base do CT-1 e reinspeções)
    // =================================================================
    private sealed record Inspection(bool Opened, string Format,
        long TotalItems, int FolderCount, int Unreadable);

    private static Inspection Inspect(string pstPath, Harness h)
    {
        try
        {
            using var pst = PersonalStorage.FromFile(pstPath, writable: false);
            long total = 0;
            var folders = 0;
            var unreadable = 0;
            foreach (var (_, folder) in EnumerateFolders(pst.RootFolder))
            {
                folders++;
                try
                {
                    total += folder.ContentCount;
                    h.AddItems(folder.ContentCount);
                }
                catch
                {
                    unreadable++;
                }
            }
            return new Inspection(true, pst.Format.ToString(), total,
                folders, unreadable);
        }
        catch
        {
            return new Inspection(false, "unknown", 0, 0, 0);
        }
    }

    /// <summary>Enumera pastas com o caminho completo relativo à raiz.</summary>
    private static IEnumerable<(string Path, FolderInfo Folder)>
        EnumerateFolders(FolderInfo root)
    {
        var stack = new Stack<(string, FolderInfo)>();
        stack.Push(("", root));
        while (stack.Count > 0)
        {
            var (path, folder) = stack.Pop();
            yield return (path, folder);
            foreach (var sub in folder.GetSubFolders())
            {
                var name = sub.DisplayName ?? "";
                stack.Push((path.Length == 0 ? name : path + "/" + name, sub));
            }
        }
    }

    private static FolderInfo EnsurePath(PersonalStorage pst, string path)
    {
        if (string.IsNullOrEmpty(path)) return pst.RootFolder;
        var current = pst.RootFolder;
        foreach (var part in path.Split('/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.GetSubFolder(part) ?? current.AddSubFolder(part);
        }
        return current;
    }

    private static (long TotalItems, int Folders)? LoadExpected(string pstPath)
    {
        var manifest = pstPath + ".expected.json";
        if (!File.Exists(manifest)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var root = doc.RootElement;
        return (root.GetProperty("totalItems").GetInt64(),
                root.GetProperty("folders").GetArrayLength());
    }
}
