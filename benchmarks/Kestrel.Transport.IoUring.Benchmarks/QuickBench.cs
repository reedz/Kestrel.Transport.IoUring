using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.RegularExpressions;
using Kestrel.Transport.IoUring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Kestrel.Transport.IoUring.Benchmarks;

public static partial class QuickBench
{
    private const int SocketPort = 16080;
    private const int IoUringPort = 16081;

    private sealed record Scenario(string Name, int Connections, bool Churn);

    private sealed record WrkResult(double Rps, double P99Ms, int Errors, string Raw);

    private sealed record Sample(
        string Scenario,
        string Transport,
        int Trial,
        double Rps,
        double CpuCores,
        double RpsPerCpu,
        double P99Ms,
        int Errors,
        int FdCount,
        double WorkingSetMb);

    private sealed record LatencySample(
        string Scenario,
        string Transport,
        int Trial,
        int OfferedRps,
        double AchievedRps,
        double P99Ms,
        int Errors);

    private sealed record Aggregate(
        string Scenario,
        string Transport,
        double Rps,
        double CpuCores,
        double RpsPerCpu,
        double P99Ms,
        int Errors);

    private static readonly Scenario[] Scenarios =
    [
        new("persistent-16", 16, false),
        new("persistent-64", 64, false),
        new("persistent-128", 128, false),
        new("persistent-256", 256, false),
        new("pooled-16", 16, false),
        new("pooled-64", 64, false),
        new("pooled-128", 128, false),
        new("churn-64", 64, true),
    ];

    public static async Task RunAsync(string[] args)
    {
        if (!Ring.IsSupported)
            throw new InvalidOperationException("io_uring is not available on this host.");
        if (!File.Exists("/usr/bin/wrk"))
            throw new InvalidOperationException("wrk is required. Install it with: apt-get install wrk");
        if (!File.Exists("/usr/local/bin/wrk2"))
            throw new InvalidOperationException(
                "wrk2 is required at /usr/local/bin/wrk2 for matched-load latency.");

        int trials = GetIntArgument(args, "--trials", 5);
        int durationSeconds = GetIntArgument(args, "--duration", 5);
        int latencyDurationSeconds = GetIntArgument(args, "--latency-duration", 10);
        int latencyRate = GetIntArgument(args, "--latency-rate", 2400);
        int spinCount = GetIntArgument(args, "--spin", 0);
        int ringSize = GetIntArgument(args, "--ring-size", 1024);
        int bufferRingSize = GetIntArgument(args, "--buffer-ring-size", 1024);
        string? scenarioFilter = GetOptionalStringArgument(args, "--scenario");
        string outputPath = GetStringArgument(
            args,
            "--output",
            Path.Combine(Environment.CurrentDirectory, "benchmark-results.csv"));
        string rawPath = Path.ChangeExtension(outputPath, ".wrk.txt");
        string latencyOutputPath = Path.ChangeExtension(outputPath, ".latency.csv");

        var affinity = ConfigureClientAffinity();
        using var socketServer = await StartServerAsync(
            "socket", SocketPort, affinity.ServerMask, ringSize, bufferRingSize, 0);
        using var ioUringServer = await StartServerAsync(
            "iouring", IoUringPort, affinity.ServerMask, ringSize, bufferRingSize, spinCount);

        Scenario[] scenarios = scenarioFilter == null
            ? Scenarios
            : Scenarios.Where(s => s.Name == scenarioFilter).ToArray();
        if (scenarios.Length == 0)
            throw new ArgumentException($"Unknown scenario '{scenarioFilter}'.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.Delete(rawPath);

        Console.WriteLine($"Trials: {trials}, duration: {durationSeconds}s");
        Console.WriteLine(
            $"Matched-load latency: {latencyRate} RPS for {latencyDurationSeconds}s");
        Console.WriteLine($"Client affinity: {affinity.ClientDescription}");
        Console.WriteLine($"Server affinity: {affinity.ServerDescription}");
        Console.WriteLine(
            $"io_uring tuning: ring={ringSize}, buffers={bufferRingSize}, spin={spinCount}");
        Console.WriteLine($"CSV output: {outputPath}");
        Console.WriteLine($"Raw wrk output: {rawPath}");
        Console.WriteLine();

        var samples = new List<Sample>(scenarios.Length * trials * 2);
        var latencySamples = new List<LatencySample>(scenarios.Length * trials * 2);
        try
        {
            foreach (var scenario in scenarios)
            {
                Console.WriteLine($"=== {scenario.Name} ({scenario.Connections} connections) ===");
                await PreconditionAsync(
                    SocketPort, scenario, rawPath, "socket", durationSeconds: 2);
                await PreconditionAsync(
                    IoUringPort, scenario, rawPath, "io_uring", durationSeconds: 2);

                for (int trial = 1; trial <= trials; trial++)
                {
                    bool ioFirst = trial % 2 == 0;
                    if (ioFirst)
                    {
                        samples.Add(await MeasureAsync(
                            ioUringServer, IoUringPort, scenario, "io_uring",
                            trial, durationSeconds, rawPath));
                        samples.Add(await MeasureAsync(
                            socketServer, SocketPort, scenario, "socket",
                            trial, durationSeconds, rawPath));
                    }
                    else
                    {
                        samples.Add(await MeasureAsync(
                            socketServer, SocketPort, scenario, "socket",
                            trial, durationSeconds, rawPath));
                        samples.Add(await MeasureAsync(
                            ioUringServer, IoUringPort, scenario, "io_uring",
                            trial, durationSeconds, rawPath));
                    }

                    foreach (var sample in samples.TakeLast(2))
                    {
                        Console.WriteLine(
                            $"  t{trial} {sample.Transport,-8} " +
                            $"{sample.Rps,10:F0} rps cpu={sample.CpuCores,5:F2} " +
                            $"rps/cpu={sample.RpsPerCpu,10:F0} p99={sample.P99Ms,7:F3}ms " +
                            $"errors={sample.Errors} fds={sample.FdCount} " +
                            $"ws={sample.WorkingSetMb:F0}MB");
                    }
                }

                Console.WriteLine($"  matched-load latency @ {latencyRate} RPS");
                await PreconditionLatencyAsync(
                    SocketPort, scenario, rawPath, "socket", latencyRate);
                await PreconditionLatencyAsync(
                    IoUringPort, scenario, rawPath, "io_uring", latencyRate);
                for (int trial = 1; trial <= trials; trial++)
                {
                    bool ioFirst = trial % 2 == 0;
                    if (ioFirst)
                    {
                        latencySamples.Add(await MeasureLatencyAsync(
                            IoUringPort, scenario, "io_uring", trial,
                            latencyDurationSeconds, latencyRate, rawPath));
                        latencySamples.Add(await MeasureLatencyAsync(
                            SocketPort, scenario, "socket", trial,
                            latencyDurationSeconds, latencyRate, rawPath));
                    }
                    else
                    {
                        latencySamples.Add(await MeasureLatencyAsync(
                            SocketPort, scenario, "socket", trial,
                            latencyDurationSeconds, latencyRate, rawPath));
                        latencySamples.Add(await MeasureLatencyAsync(
                            IoUringPort, scenario, "io_uring", trial,
                            latencyDurationSeconds, latencyRate, rawPath));
                    }

                    foreach (var sample in latencySamples.TakeLast(2))
                    {
                        Console.WriteLine(
                            $"    t{trial} {sample.Transport,-8} " +
                            $"achieved={sample.AchievedRps,7:F0} " +
                            $"p99={sample.P99Ms,7:F3}ms errors={sample.Errors}");
                    }
                }
                Console.WriteLine();
            }
        }
        finally
        {
            StopServer(ioUringServer);
            StopServer(socketServer);
        }

        await WriteCsvAsync(outputPath, samples);
        await WriteLatencyCsvAsync(latencyOutputPath, latencySamples);
        bool passed = PrintSummaryAndGate(
            AggregateSamples(samples),
            samples,
            latencySamples,
            scenarios);
        if (!passed)
            Environment.ExitCode = 2;
    }

    public static async Task RunServerAsync(
        string transport,
        int port,
        int ringSize = 1024,
        int bufferRingSize = 1024)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        if (transport == "iouring")
        {
            builder.WebHost.UseIoUring(options =>
            {
                options.RingSize = ringSize;
                options.BufferRingSize = bufferRingSize;
            });
        }
        else if (transport != "socket")
        {
            throw new ArgumentException($"Unknown transport '{transport}'.", nameof(transport));
        }

        var app = builder.Build();
        app.MapGet("/", () => "OK");
        await app.StartAsync();
        Console.WriteLine("READY");
        Console.Out.Flush();
        await app.WaitForShutdownAsync();
    }

    private static async Task PreconditionAsync(
        int port,
        Scenario scenario,
        string rawPath,
        string transport,
        int durationSeconds)
    {
        WrkResult result = await RunWrkAsync(
            "/usr/bin/wrk", port, scenario, durationSeconds, rate: null);
        await AppendRawAsync(rawPath, $"warmup {scenario.Name} {transport}", result.Raw);
        if (result.Errors != 0)
            throw new InvalidOperationException(
                $"Preconditioning for {scenario.Name}/{transport} had {result.Errors} errors.");
    }

    private static async Task<Sample> MeasureAsync(
        Process server,
        int port,
        Scenario scenario,
        string transport,
        int trial,
        int durationSeconds,
        string rawPath)
    {
        server.Refresh();
        TimeSpan cpuBefore = server.TotalProcessorTime;
        long started = Stopwatch.GetTimestamp();
        WrkResult wrk = await RunWrkAsync(
            "/usr/bin/wrk", port, scenario, durationSeconds, rate: null);
        double elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        server.Refresh();

        double cpuSeconds = (server.TotalProcessorTime - cpuBefore).TotalSeconds;
        double cpuCores = cpuSeconds / elapsedSeconds;
        double rpsPerCpu = cpuCores > 0 ? wrk.Rps / cpuCores : double.PositiveInfinity;
        await AppendRawAsync(
            rawPath,
            $"trial {trial} {scenario.Name} {transport}",
            wrk.Raw);

        return new Sample(
            scenario.Name,
            transport,
            trial,
            wrk.Rps,
            cpuCores,
            rpsPerCpu,
            wrk.P99Ms,
            wrk.Errors,
            CountFileDescriptors(server.Id),
            server.WorkingSet64 / 1024.0 / 1024.0);
    }

    private static async Task PreconditionLatencyAsync(
        int port,
        Scenario scenario,
        string rawPath,
        string transport,
        int rate)
    {
        WrkResult result = await RunWrkAsync(
            "/usr/local/bin/wrk2", port, scenario, durationSeconds: 1, rate);
        await AppendRawAsync(
            rawPath,
            $"matched warmup {scenario.Name} {transport} {rate}rps",
            result.Raw);
        if (result.Errors != 0)
            throw new InvalidOperationException(
                $"Matched-load warmup for {scenario.Name}/{transport} had {result.Errors} errors.");
    }

    private static async Task<LatencySample> MeasureLatencyAsync(
        int port,
        Scenario scenario,
        string transport,
        int trial,
        int durationSeconds,
        int rate,
        string rawPath)
    {
        WrkResult result = await RunWrkAsync(
            "/usr/local/bin/wrk2", port, scenario, durationSeconds, rate);
        await AppendRawAsync(
            rawPath,
            $"matched trial {trial} {scenario.Name} {transport} {rate}rps",
            result.Raw);
        return new LatencySample(
            scenario.Name,
            transport,
            trial,
            rate,
            result.Rps,
            result.P99Ms,
            result.Errors);
    }

    private static async Task<WrkResult> RunWrkAsync(
        string executable,
        int port,
        Scenario scenario,
        int durationSeconds,
        int? rate)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        int threads = rate.HasValue
            ? Math.Min(4, Math.Max(1, (scenario.Connections + 63) / 64))
            : 1;
        startInfo.ArgumentList.Add($"-t{threads}");
        startInfo.ArgumentList.Add($"-c{scenario.Connections}");
        startInfo.ArgumentList.Add($"-d{durationSeconds}s");
        startInfo.ArgumentList.Add("--latency");
        if (rate.HasValue)
        {
            startInfo.ArgumentList.Add("-R");
            startInfo.ArgumentList.Add(rate.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (scenario.Churn)
        {
            startInfo.ArgumentList.Add("-H");
            startInfo.ArgumentList.Add("Connection: close");
        }
        startInfo.ArgumentList.Add($"http://127.0.0.1:{port}/");

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start wrk.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string raw = output + (string.IsNullOrWhiteSpace(error) ? "" : $"\nSTDERR:\n{error}");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"wrk exited with code {process.ExitCode}:\n{raw}");

        Match rpsMatch = RequestsPerSecondRegex().Match(output);
        Match p99Match = P99Regex().Match(output);
        if (!rpsMatch.Success || !p99Match.Success)
            throw new InvalidOperationException($"Unable to parse wrk output:\n{output}");

        int errors = SocketErrorsRegex().Matches(output)
            .Select(match => match.Groups.Values.Skip(1).Sum(group =>
                int.TryParse(group.Value, out int value) ? value : 0))
            .Sum();
        Match non2xx = Non2xxRegex().Match(output);
        if (non2xx.Success)
            errors += int.Parse(non2xx.Groups[1].Value, CultureInfo.InvariantCulture);

        return new WrkResult(
            double.Parse(rpsMatch.Groups[1].Value, CultureInfo.InvariantCulture),
            ParseDurationMs(p99Match.Groups[1].Value),
            errors,
            raw);
    }

    private static double ParseDurationMs(string value)
    {
        Match match = DurationRegex().Match(value.Trim());
        if (!match.Success)
            throw new InvalidOperationException($"Unable to parse latency '{value}'.");
        double number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch
        {
            "us" => number / 1000,
            "ms" => number,
            "s" => number * 1000,
            "m" => number * 60_000,
            _ => throw new InvalidOperationException($"Unknown latency unit in '{value}'."),
        };
    }

    private static async Task<Process> StartServerAsync(
        string transport,
        int port,
        nint affinityMask,
        int ringSize,
        int bufferRingSize,
        int spinCount)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(QuickBench).Assembly.Location);
        startInfo.ArgumentList.Add("server");
        startInfo.ArgumentList.Add(transport);
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(ringSize.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(bufferRingSize.ToString(CultureInfo.InvariantCulture));
        startInfo.Environment["DOTNET_gcServer"] = "1";
        if (transport == "iouring" && spinCount > 0)
            startInfo.Environment["IOURING_SPIN_COUNT"] =
                spinCount.ToString(CultureInfo.InvariantCulture);

        var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Failed to start {transport} server.");
        if (affinityMask != 0 && OperatingSystem.IsLinux())
            process.ProcessorAffinity = affinityMask;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                Console.Error.WriteLine($"[{transport}] {e.Data}");
        };
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line == "READY")
                return process;
            if (line == null)
                throw new InvalidOperationException($"{transport} server exited before readiness.");
        }
    }

    private static List<Aggregate> AggregateSamples(List<Sample> samples) =>
        samples
            .GroupBy(s => (s.Scenario, s.Transport))
            .Select(group => new Aggregate(
                group.Key.Scenario,
                group.Key.Transport,
                Median(group.Select(x => x.Rps)),
                Median(group.Select(x => x.CpuCores)),
                Median(group.Select(x => x.RpsPerCpu)),
                Median(group.Select(x => x.P99Ms)),
                group.Sum(x => x.Errors)))
            .OrderBy(x => Array.FindIndex(Scenarios, s => s.Name == x.Scenario))
            .ThenBy(x => x.Transport)
            .ToList();

    private static bool PrintSummaryAndGate(
        List<Aggregate> aggregates,
        List<Sample> samples,
        List<LatencySample> latencySamples,
        Scenario[] scenarios)
    {
        bool passed = true;
        Console.WriteLine("=== Per-cell medians ===");
        Console.WriteLine("| Scenario | Socket RPS | io_uring RPS | Socket RPS/CPU | io_uring RPS/CPU | Socket p99 | io_uring p99 | Errors |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        var socketCells = new List<Aggregate>(scenarios.Length);
        var ioCells = new List<Aggregate>(scenarios.Length);
        foreach (var scenario in scenarios)
        {
            var socket = aggregates.Single(x =>
                x.Scenario == scenario.Name && x.Transport == "socket");
            var io = aggregates.Single(x =>
                x.Scenario == scenario.Name && x.Transport == "io_uring");
            socketCells.Add(socket);
            ioCells.Add(io);
            Console.WriteLine(
                $"| {scenario.Name} | {socket.Rps:F0} | {io.Rps:F0} | " +
                $"{socket.RpsPerCpu:F0} | {io.RpsPerCpu:F0} | " +
                $"{socket.P99Ms:F3} ms | {io.P99Ms:F3} ms | " +
                $"{socket.Errors + io.Errors} |");
        }

        Console.WriteLine();
        Console.WriteLine("=== Matched-load p99 medians ===");
        Console.WriteLine("| Scenario | Socket achieved | io_uring achieved | Socket p99 | io_uring p99 | Gate |");
        Console.WriteLine("|---|---:|---:|---:|---:|---|");
        bool everyCellPassed = true;
        foreach (var scenario in scenarios)
        {
            var socket = latencySamples.Where(x =>
                x.Scenario == scenario.Name && x.Transport == "socket").ToArray();
            var io = latencySamples.Where(x =>
                x.Scenario == scenario.Name && x.Transport == "io_uring").ToArray();
            var socketThroughput = socketCells.Single(x => x.Scenario == scenario.Name);
            var ioThroughput = ioCells.Single(x => x.Scenario == scenario.Name);
            double socketAchieved = Median(socket.Select(x => x.AchievedRps));
            double ioAchieved = Median(io.Select(x => x.AchievedRps));
            double offered = socket[0].OfferedRps;
            double socketP99Cell = Median(socket.Select(x => x.P99Ms));
            double ioP99Cell = Median(io.Select(x => x.P99Ms));
            bool cellPassed =
                ioThroughput.Rps > socketThroughput.Rps &&
                ioThroughput.RpsPerCpu > socketThroughput.RpsPerCpu &&
                ioP99Cell <= socketP99Cell &&
                socketAchieved >= offered * 0.9 &&
                ioAchieved >= offered * 0.9 &&
                socket.Sum(x => x.Errors) + io.Sum(x => x.Errors) == 0;
            everyCellPassed &= cellPassed;
            Console.WriteLine(
                $"| {scenario.Name} | {socketAchieved:F0} | {ioAchieved:F0} | " +
                $"{socketP99Cell:F3} ms | {ioP99Cell:F3} ms | " +
                $"{(cellPassed ? "PASS" : "FAIL")} |");
        }

        var socketSamples = samples.Where(x => x.Transport == "socket").ToArray();
        var ioSamples = samples.Where(x => x.Transport == "io_uring").ToArray();
        var socketLatency = latencySamples.Where(x => x.Transport == "socket").ToArray();
        var ioLatency = latencySamples.Where(x => x.Transport == "io_uring").ToArray();
        double socketRps = Median(socketSamples.Select(x => x.Rps));
        double ioRps = Median(ioSamples.Select(x => x.Rps));
        double socketRpsPerCpu = Median(socketSamples.Select(x => x.RpsPerCpu));
        double ioRpsPerCpu = Median(ioSamples.Select(x => x.RpsPerCpu));
        double socketP99 = Median(socketLatency.Select(x => x.P99Ms));
        double ioP99 = Median(ioLatency.Select(x => x.P99Ms));
        int errors = samples.Sum(x => x.Errors) + latencySamples.Sum(x => x.Errors);
        passed =
            everyCellPassed &&
            ioRps > socketRps &&
            ioRpsPerCpu > socketRpsPerCpu &&
            ioP99 <= socketP99 &&
            errors == 0;

        Console.WriteLine();
        Console.WriteLine("=== Matrix median gate ===");
        Console.WriteLine($"RPS:     socket={socketRps:F0}, io_uring={ioRps:F0}");
        Console.WriteLine(
            $"RPS/CPU: socket={socketRpsPerCpu:F0}, io_uring={ioRpsPerCpu:F0}");
        Console.WriteLine(
            $"matched p99: socket={socketP99:F3} ms, io_uring={ioP99:F3} ms");
        Console.WriteLine($"errors:  {errors}");
        Console.WriteLine(passed ? "PERFORMANCE GATE PASSED" : "PERFORMANCE GATE FAILED");
        return passed;
    }

    private static async Task WriteCsvAsync(string path, IEnumerable<Sample> samples)
    {
        await using var writer = new StreamWriter(path, append: false);
        await writer.WriteLineAsync(
            "scenario,transport,trial,rps,cpu_cores,rps_per_cpu,p99_ms,errors,fd_count,working_set_mb");
        foreach (var sample in samples)
        {
            await writer.WriteLineAsync(string.Join(',',
                sample.Scenario,
                sample.Transport,
                sample.Trial.ToString(CultureInfo.InvariantCulture),
                sample.Rps.ToString("F3", CultureInfo.InvariantCulture),
                sample.CpuCores.ToString("F6", CultureInfo.InvariantCulture),
                sample.RpsPerCpu.ToString("F3", CultureInfo.InvariantCulture),
                sample.P99Ms.ToString("F6", CultureInfo.InvariantCulture),
                sample.Errors.ToString(CultureInfo.InvariantCulture),
                sample.FdCount.ToString(CultureInfo.InvariantCulture),
                sample.WorkingSetMb.ToString("F3", CultureInfo.InvariantCulture)));
        }
    }

    private static async Task WriteLatencyCsvAsync(
        string path,
        IEnumerable<LatencySample> samples)
    {
        await using var writer = new StreamWriter(path, append: false);
        await writer.WriteLineAsync(
            "scenario,transport,trial,offered_rps,achieved_rps,p99_ms,errors");
        foreach (var sample in samples)
        {
            await writer.WriteLineAsync(string.Join(',',
                sample.Scenario,
                sample.Transport,
                sample.Trial.ToString(CultureInfo.InvariantCulture),
                sample.OfferedRps.ToString(CultureInfo.InvariantCulture),
                sample.AchievedRps.ToString("F3", CultureInfo.InvariantCulture),
                sample.P99Ms.ToString("F6", CultureInfo.InvariantCulture),
                sample.Errors.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static async Task AppendRawAsync(string path, string label, string raw)
    {
        await File.AppendAllTextAsync(
            path,
            $"\n===== {label} =====\n{raw.TrimEnd()}\n");
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static (nint ServerMask, string ServerDescription, string ClientDescription)
        ConfigureClientAffinity()
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return (0, "unrestricted", "unrestricted");

            var current = Process.GetCurrentProcess();
            long allowed = (long)current.ProcessorAffinity;
            long[] bits = Enumerable.Range(0, 63)
                .Select(bit => 1L << bit)
                .Where(bit => (allowed & bit) != 0)
                .Take(2)
                .ToArray();
            if (bits.Length < 2)
                return (0, "unrestricted", "unrestricted");

            current.ProcessorAffinity = (nint)bits[1];
            return (
                (nint)bits[0],
                $"CPU {BitOperations.TrailingZeroCount((ulong)bits[0])}",
                $"CPU {BitOperations.TrailingZeroCount((ulong)bits[1])}");
        }
        catch
        {
            return (0, "unrestricted", "unrestricted");
        }
    }

    private static int CountFileDescriptors(int processId)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries($"/proc/{processId}/fd").Count();
        }
        catch
        {
            return -1;
        }
    }

    private static void StopServer(Process process)
    {
        if (process.HasExited)
            return;
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }

    private static int GetIntArgument(string[] args, string name, int defaultValue)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length &&
            int.TryParse(args[index + 1], out int value)
            ? value
            : defaultValue;
    }

    private static string GetStringArgument(string[] args, string name, string defaultValue)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : defaultValue;
    }

    private static string? GetOptionalStringArgument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    [GeneratedRegex(@"Requests/sec:\s+([0-9.]+)", RegexOptions.Multiline)]
    private static partial Regex RequestsPerSecondRegex();

    [GeneratedRegex(
        @"^\s*99(?:\.000)?%\s+([0-9.]+(?:us|ms|s|m))\s*$",
        RegexOptions.Multiline)]
    private static partial Regex P99Regex();

    [GeneratedRegex(
        @"Socket errors:\s+connect\s+(\d+),\s+read\s+(\d+),\s+write\s+(\d+),\s+timeout\s+(\d+)",
        RegexOptions.Multiline)]
    private static partial Regex SocketErrorsRegex();

    [GeneratedRegex(@"Non-2xx or 3xx responses:\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex Non2xxRegex();

    [GeneratedRegex(@"^([0-9.]+)(us|ms|s|m)$")]
    private static partial Regex DurationRegex();
}
