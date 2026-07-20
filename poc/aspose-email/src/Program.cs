// Harness descartável da PoC Aspose (ADR-0004, item 1 do gate).
// Compilado apenas na VM descartável via scripts/bootstrap.ps1 — este
// diretório nunca integra a solution do produto.
//
// Comandos:
//   corpus --out DIR --scale smoke|full [--seed 42]
//   ct1 --pst FILE --results DIR --metrics DIR
//   ct2 --pst FILE --workdir DIR --results DIR --metrics DIR
//   ct3 --pst FILE --workdir DIR --results DIR --metrics DIR
//   ct4 --pst FILE --results DIR --metrics DIR
//   ct5 --corpus DIR --results DIR --metrics DIR

using System;
using System.Collections.Generic;
using System.IO;
using Aspose.Email;

namespace AsposePoc;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("uso: AsposePoc <corpus|ct1|ct2|ct3|ct4|ct5> [opções]");
            return 2;
        }

        var licensePath = Environment.GetEnvironmentVariable("ASPOSE_POC_LICENSE");
        if (!string.IsNullOrEmpty(licensePath) && File.Exists(licensePath))
        {
            new License().SetLicense(licensePath);
        }
        else
        {
            Console.Error.WriteLine(
                "AVISO: ASPOSE_POC_LICENSE ausente — rodando em modo avaliação " +
                "(marcas d'água invalidam CT-3; registre no relatório).");
        }

        var opts = ParseOptions(args);
        try
        {
            return args[0] switch
            {
                "corpus" => CorpusGenerator.Run(opts),
                "ct1" => Cases.RunCt1(opts),
                "ct2" => Cases.RunCt2(opts),
                "ct3" => Cases.RunCt3(opts),
                "ct4" => Cases.RunCt4(opts),
                "ct5" => Cases.RunCt5(opts),
                _ => Fail($"comando desconhecido: {args[0]}"),
            };
        }
        catch (Exception ex)
        {
            // Falha não estruturada é, por definição, um crash do harness:
            // o caso correspondente deve registrar crashes=1 via Harness.
            Console.Error.WriteLine($"CRASH: {ex}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length - 1; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"opção inválida: {args[i]}");
            opts[args[i][2..]] = args[i + 1];
        }
        return opts;
    }
}
