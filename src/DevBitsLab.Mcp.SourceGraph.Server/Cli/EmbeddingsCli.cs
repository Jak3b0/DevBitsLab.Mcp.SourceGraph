using DevBitsLab.Mcp.SourceGraph.Embeddings;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// CLI surface for the embedding model cache: <c>status</c>, <c>pull</c>, <c>remove</c>,
/// <c>verify</c>. Mirrors <see cref="ScopesCli"/> / <see cref="PluginsCli"/> / <see cref="VocabularyCli"/>:
/// each invocation builds its own logger factory and <see cref="EmbeddingsManager"/> (no DI
/// scope on the one-shot CLI path) and renders the result to stdout.
/// </summary>
internal static class EmbeddingsCli
{
    public static async Task<int> RunSubcommandAsync(CommandLine cli)
    {
        var sub = cli.Positional.Count > 0 ? cli.Positional[0] : "";
        return sub switch
        {
            "status" => await RunStatusAsync(cli).ConfigureAwait(false),
            "pull"   => await RunPullAsync(cli).ConfigureAwait(false),
            "remove" => await RunRemoveAsync(cli).ConfigureAwait(false),
            "verify" => await RunVerifyAsync(cli).ConfigureAwait(false),
            _        => Unknown(sub),
        };
    }

    private static EmbeddingsManager BuildManager(CommandLine cli, out ILoggerFactory loggerFactory)
    {
        loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        });
        var modelInfo = new EmbeddingModelInfo(cli.Model ?? DefaultEmbeddingModel.ModelId, DefaultEmbeddingModel.Dimension);
        var store = new ModelStore(loggerFactory.CreateLogger<ModelStore>());
        return new EmbeddingsManager(store, modelInfo, loggerFactory.CreateLogger<EmbeddingsManager>());
    }

    private static async Task<int> RunStatusAsync(CommandLine cli)
    {
        var mgr = BuildManager(cli, out var loggerFactory);
        using (loggerFactory)
        {
            var status = await mgr.GetStatusAsync(cli.Model).ConfigureAwait(false);
            PrintStatus(status, verifying: false);
            return 0;
        }
    }

    private static async Task<int> RunPullAsync(CommandLine cli)
    {
        var mgr = BuildManager(cli, out var loggerFactory);
        using (loggerFactory)
        {
            try
            {
                var status = await mgr.PullAsync(cli.Model).ConfigureAwait(false);
                Console.WriteLine();
                PrintStatus(status, verifying: false);
                return 0;
            }
            catch (ModelDownloadException ex)
            {
                await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }
    }

    private static async Task<int> RunRemoveAsync(CommandLine cli)
    {
        if (cli.Model is not null && cli.All)
        {
            await Console.Error.WriteLineAsync(
                "error: --model and --all cannot be combined; pick exactly one.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                "usage: sourcegraph-mcp embeddings remove [--model <id>] [--all]").ConfigureAwait(false);
            return 2;
        }
        var mgr = BuildManager(cli, out var loggerFactory);
        using (loggerFactory)
        {
            var result = await mgr.RemoveAsync(cli.Model, cli.All).ConfigureAwait(false);
            if (result.RemovedDirs.Count == 0)
            {
                Console.WriteLine($"nothing to remove (cache empty for {result.ModelId ?? "all models"})");
                return 0;
            }
            Console.WriteLine($"removed {result.RemovedDirs.Count} director{(result.RemovedDirs.Count == 1 ? "y" : "ies")}, freed {FormatBytes(result.FreedBytes)}:");
            foreach (var dir in result.RemovedDirs)
            {
                Console.WriteLine($"  - {dir}");
            }
            return 0;
        }
    }

    private static async Task<int> RunVerifyAsync(CommandLine cli)
    {
        var mgr = BuildManager(cli, out var loggerFactory);
        using (loggerFactory)
        {
            var status = await mgr.VerifyAsync(cli.Model).ConfigureAwait(false);
            PrintStatus(status, verifying: true);
            // Exit non-zero only when at least one file has Match = false (genuine mismatch);
            // Match = null is informational mode (no pinned SHA), exit 0.
            var anyMismatch = status.Files.Any(f => f.Match == false);
            return anyMismatch ? 2 : 0;
        }
    }

    private static void PrintStatus(EmbeddingsStatus status, bool verifying)
    {
        Console.WriteLine($"model:      {status.ModelId} (dim={status.Dimension})");
        Console.WriteLine($"cache dir:  {status.CacheDir}");
        Console.WriteLine($"free disk:  {(status.FreeDiskBytes is null ? "unknown" : FormatBytes(status.FreeDiskBytes.Value))}");
        Console.WriteLine();
        if (status.Files.Count == 0)
        {
            Console.WriteLine("(manifest is empty for this model)");
            return;
        }
        Console.WriteLine($"{"file",-20} {"present",-7} {"size",-12} {"sha-256",-66} {"match"}");
        foreach (var f in status.Files)
        {
            var size = f.SizeBytes is null ? "-" : FormatBytes(f.SizeBytes.Value);
            var sha = f.ComputedSha ?? "-";
            var matchCol = !verifying ? "-"
                : f.Match switch
                {
                    null when f.Present => "info-only",  // no pinned SHA in manifest
                    null => "-",
                    true => "ok",
                    _    => "MISMATCH", // bool?.false — discard avoids CodeQL `cs/constant-condition` on the redundant constant arm
                };
            Console.WriteLine($"{f.LocalName,-20} {(f.Present ? "yes" : "no"),-7} {size,-12} {sha,-66} {matchCol}");
        }
        if (verifying && status.Files.Any(f => f.Present && f.PinnedSha is null))
        {
            Console.WriteLine();
            Console.WriteLine("note: no pinned SHA in manifest — informational only.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double KiB = 1024;
        const double MiB = KiB * 1024;
        const double GiB = MiB * 1024;
        return bytes switch
        {
            < (long)KiB => $"{bytes} B",
            < (long)MiB => $"{bytes / KiB:F1} KiB",
            < (long)GiB => $"{bytes / MiB:F1} MiB",
            _           => $"{bytes / GiB:F2} GiB",
        };
    }

    private static int Unknown(string sub)
    {
        if (string.IsNullOrEmpty(sub))
        {
            Console.Error.WriteLine("usage: sourcegraph-mcp embeddings [status|pull|remove|verify]");
        }
        else
        {
            Console.Error.WriteLine($"Unknown embeddings subcommand: {sub}");
            Console.Error.WriteLine("usage: sourcegraph-mcp embeddings [status|pull|remove|verify]");
        }
        return 2;
    }
}
