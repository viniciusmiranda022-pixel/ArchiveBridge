// Gerador de corpus sintético (§45.2 do runbook / seção 3 do plano).
//
// Metodologia: o plano de conteúdo (pastas, itens, tamanhos, datas) é
// gerado de forma determinística ANTES de tocar a engine; o manifesto
// *.expected.json nasce do plano, nunca da leitura do PST. Assim a
// verificação dos CTs compara a engine contra o plano, não contra ela
// mesma. Anomalias são derivadas por manipulação binária externa à engine.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Aspose.Email.Mapi;
using Aspose.Email.Storage.Pst;

namespace AsposePoc;

public sealed record PlannedItem(
    string FolderPath, string Subject, DateTime ReceivedUtc,
    string MessageClass, int AttachmentBytes);

public sealed record PstPlan(
    string Name, bool Ansi, long TargetBytes, List<PlannedItem> Items);

public static class CorpusGenerator
{
    public static int Run(Dictionary<string, string> opts)
    {
        var outDir = opts["out"];
        var scale = opts.TryGetValue("scale", out var s) ? s : "smoke";
        var seed = ulong.Parse(opts.TryGetValue("seed", out var sd) ? sd : "42");
        Directory.CreateDirectory(outDir);

        foreach (var plan in BuildPlans(scale, seed))
        {
            Console.WriteLine($"corpus: gerando {plan.Name} " +
                              $"({plan.Items.Count} itens planejados)...");
            var pstPath = Path.Combine(outDir, plan.Name + ".pst");
            Materialize(plan, pstPath, seed);
            WriteManifest(plan, pstPath);
            Console.WriteLine($"corpus: {plan.Name} ok " +
                              $"({new FileInfo(pstPath).Length / 1048576} MB)");
        }
        DeriveAnomalies(outDir, seed);
        Console.WriteLine("corpus: concluído.");
        return 0;
    }

    // -----------------------------------------------------------------
    private static IEnumerable<PstPlan> BuildPlans(string scale, ulong seed)
    {
        // smoke: valida o harness. full: adiciona os tamanhos da §45.2.
        yield return ContentPlan("c-unicode-small", false, seed, folders: 12,
            itemsPerFolder: 50, attachmentBytes: 64 * 1024);
        yield return ContentPlan("c-ansi-small", true, seed + 1, folders: 5,
            itemsPerFolder: 30, attachmentBytes: 32 * 1024);
        yield return StructurePlan("c-structure", seed + 2);
        if (scale == "full")
        {
            yield return SizedPlan("c50gb", seed + 3, 50L << 30);
            yield return SizedPlan("c100gb", seed + 4, 100L << 30);
            yield return SizedPlan("c500gb", seed + 5, 500L << 30);
        }
    }

    private static PstPlan ContentPlan(string name, bool ansi, ulong seed,
        int folders, int itemsPerFolder, int attachmentBytes)
    {
        var rng = new DeterministicRandom(seed);
        var classes = new[]
        {
            "IPM.Note", "IPM.Appointment", "IPM.Contact", "IPM.Task",
            "IPM.StickyNote", "IPM.DistList",
        };
        var items = new List<PlannedItem>();
        for (var f = 0; f < folders; f++)
        {
            var path = $"Raiz/Pasta {f:D3}";
            for (var i = 0; i < itemsPerFolder; i++)
            {
                items.Add(new PlannedItem(
                    path,
                    $"Item {f:D3}-{i:D4} [{rng.NextULong():x8}]",
                    BaseDate(rng),
                    classes[rng.Next(classes.Length)],
                    rng.Next(4) == 0 ? attachmentBytes : 0));
            }
        }
        return new PstPlan(name, ansi, 0, items);
    }

    private static PstPlan StructurePlan(string name, ulong seed)
    {
        // Pastas profundas, Unicode, conflito de case, pasta gigante,
        // datas extremas/ausentes, anexo grande, custom props (via classe).
        var rng = new DeterministicRandom(seed);
        var items = new List<PlannedItem>();
        var deep = string.Join('/', Enumerable.Range(0, 20)
            .Select(i => $"N{i:D2}"));
        items.Add(new PlannedItem($"Raiz/{deep}", "Item profundo",
            BaseDate(rng), "IPM.Note", 0));
        items.Add(new PlannedItem("Raiz/Ünïcödê 郵便 📁", "Assunto Unicode ✉",
            BaseDate(rng), "IPM.Note", 0));
        items.Add(new PlannedItem("Raiz/casesensitive", "minúscula",
            BaseDate(rng), "IPM.Note", 0));
        items.Add(new PlannedItem("Raiz/CaseSensitive", "MAIÚSCULA",
            BaseDate(rng), "IPM.Note", 0));
        for (var i = 0; i < 100_000; i++)
        {
            items.Add(new PlannedItem("Raiz/PastaGigante",
                $"Volume {i:D6}", BaseDate(rng), "IPM.Note", 0));
        }
        items.Add(new PlannedItem("Raiz/Datas", "Sem data",
            DateTime.MinValue, "IPM.Note", 0));
        items.Add(new PlannedItem("Raiz/Datas", "Antiga",
            new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "IPM.Note", 0));
        items.Add(new PlannedItem("Raiz/Datas", "Futura",
            new DateTime(2099, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            "IPM.Note", 0));
        items.Add(new PlannedItem("Raiz/Anexos", "Anexo grande 1GiB",
            BaseDate(rng), "IPM.Note", 1 << 30));
        return new PstPlan(name, false, 0, items);
    }

    private static PstPlan SizedPlan(string name, ulong seed, long targetBytes)
    {
        // Tamanho atingido por anexos de 8 MiB determinísticos.
        var rng = new DeterministicRandom(seed);
        const int attach = 8 << 20;
        var count = (int)(targetBytes / attach);
        var items = new List<PlannedItem>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(new PlannedItem(
                $"Raiz/Bloco {i / 5000:D4}",
                $"Payload {i:D7} [{rng.NextULong():x8}]",
                BaseDate(rng), "IPM.Note", attach));
        }
        return new PstPlan(name, false, targetBytes, items);
    }

    private static DateTime BaseDate(DeterministicRandom rng) =>
        new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMinutes(rng.Next(3_000_000));

    // -----------------------------------------------------------------
    private static void Materialize(PstPlan plan, string pstPath, ulong seed)
    {
        var payloadRng = new DeterministicRandom(seed ^ 0xA5A5A5A5UL);
        using var pst = PersonalStorage.Create(pstPath,
            plan.Ansi ? FileFormatVersion.ANSI : FileFormatVersion.Unicode);
        var folders = new Dictionary<string, FolderInfo>(StringComparer.Ordinal);

        FolderInfo GetFolder(string path)
        {
            if (folders.TryGetValue(path, out var found)) return found;
            var parts = path.Split('/');
            var current = pst.RootFolder;
            var acc = "";
            foreach (var part in parts)
            {
                acc = acc.Length == 0 ? part : acc + "/" + part;
                if (!folders.TryGetValue(acc, out var next))
                {
                    next = current.GetSubFolder(part) ?? current.AddSubFolder(part);
                    folders[acc] = next;
                }
                current = next;
            }
            return current;
        }

        foreach (var group in plan.Items.GroupBy(i => i.FolderPath))
        {
            var folder = GetFolder(group.Key);
            foreach (var item in group)
            {
                var msg = new MapiMessage(
                    "poc-sender@example.invalid",
                    "poc-recipient@example.invalid",
                    item.Subject,
                    $"Corpo sintético de {item.Subject}.");
                msg.SetMessageFlags(MapiMessageFlags.MSGFLAG_READ);
                if (item.MessageClass != "IPM.Note")
                    msg.MessageClass = item.MessageClass;
                if (item.ReceivedUtc != DateTime.MinValue)
                    msg.DeliveryTime = item.ReceivedUtc;
                if (item.AttachmentBytes > 0)
                {
                    var payload = new byte[item.AttachmentBytes];
                    payloadRng.Fill(payload);
                    msg.Attachments.Add("payload.bin", payload);
                }
                folder.AddMessage(msg);
            }
        }
    }

    private static void WriteManifest(PstPlan plan, string pstPath)
    {
        var manifest = new
        {
            name = plan.Name,
            ansi = plan.Ansi,
            totalItems = plan.Items.Count,
            folders = plan.Items
                .GroupBy(i => i.FolderPath)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { path = g.Key, items = g.Count() }),
            classes = plan.Items
                .GroupBy(i => i.MessageClass)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            fingerprintSample = plan.Items
                .Where((_, idx) => idx % 997 == 0)
                .Select(i => Harness.Sha256Text(
                    $"{i.FolderPath}|{i.Subject}|{i.ReceivedUtc:O}")),
        };
        File.WriteAllText(pstPath + ".expected.json",
            JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(pstPath + ".sha256", Harness.Sha256File(pstPath));
    }

    // -----------------------------------------------------------------
    private static void DeriveAnomalies(string outDir, ulong seed)
    {
        // Manipulação binária de CÓPIAS, fora da engine (§22, CT-5).
        var source = Path.Combine(outDir, "c-unicode-small.pst");
        if (!File.Exists(source)) return;
        var rng = new DeterministicRandom(seed ^ 0x5F5F5F5FUL);

        var truncated = Path.Combine(outDir, "a-truncated.pst");
        var bytes = File.ReadAllBytes(source);
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 3)]);

        var corrupted = Path.Combine(outDir, "a-corrupted.pst");
        var corrupt = (byte[])bytes.Clone();
        for (var i = 0; i < 64; i++)
            corrupt[16384 + rng.Next(corrupt.Length - 32768)] ^= 0xFF;
        File.WriteAllBytes(corrupted, corrupt);

        foreach (var p in new[] { truncated, corrupted })
            File.WriteAllText(p + ".sha256", Harness.Sha256File(p));

        Console.WriteLine(
            "corpus: anomalias binárias geradas (a-truncated, a-corrupted). " +
            "Caso senha: gerar manualmente se a API suportar proteção por " +
            "senha na criação; registrar no relatório se ficar de fora.");
    }
}
