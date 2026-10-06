using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VbaTools.SourceIdentities;

return await SourceIdentityRepro.RunAsync(args);

internal static class SourceIdentityRepro
{
    private const int MaximumTrials = 20;
    private const int MaximumIterations = 5_000_000;
    private const int TrialTimeoutMilliseconds = 300_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "--child")
        {
            var childOptions = ParseOptions(args[1..], child: true);
            var childInputBytes = File.ReadAllBytes(Required(childOptions, "--input"));
            return RunChild(ReadPair(childInputBytes), Hash(childInputBytes),
                Required(childOptions, "--expected-sha"),
                BoundedNumber(childOptions, "--iterations", MaximumIterations));
        }
        try
        {
            var options = ParseOptions(args, child: false);
            var inputPath = Required(options, "--input");
            var inputBytes = File.ReadAllBytes(inputPath);
            var inputHash = Hash(inputBytes);
            var pair = ReadPair(inputBytes);

            var trials = BoundedNumber(options, "--trials", MaximumTrials);
            var iterations = BoundedNumber(options, "--iterations", MaximumIterations);
            var reportPath = PrepareReportPath(Required(options, "--report"));
            var report = new Report
            {
                StartedUtc = DateTimeOffset.UtcNow,
                Input = new InputIdentity(pair.Kind, pair.Provenance, inputHash,
                    pair.First.Length, HashUtf16(pair.First), pair.Second.Length, HashUtf16(pair.Second)),
                Invocation = new Invocation(trials, iterations, TrialTimeoutMilliseconds, "direct-string-pair"),
                Environment = CaptureEnvironment(),
                Trials = []
            };
            Save(reportPath, report, overwrite: false);
            for (var index = 1; index <= trials; index++)
            {
                report.Trials.Add(await RunTrialAsync(index, iterations, inputPath, inputHash));
                Save(reportPath, report, overwrite: true);
            }
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.Complete = true;
            Save(reportPath, report, overwrite: true);
            var failed = report.Trials.Count(trial => trial.Status != "passed");
            Console.WriteLine($"trials={trials} passed={trials - failed} failed={failed} report={reportPath}");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Reproducer setup failed: {error.GetType().Name}: {error.Message}");
            return 2;
        }
    }

    private static int RunChild(UriPair pair, string actualHash, string expectedHash, int iterations)
    {
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The input file changed after the parent captured its hash.");
        Console.WriteLine($"START {Environment.ProcessId} {FileHash(typeof(SourceIdentity).Assembly.Location)}");
        Console.Out.Flush();
        long checksum = 0;
        for (var index = 0; index < iterations; index++)
        {
            if (!SourceIdentity.TryFromUri(pair.First, out var first)
                || !SourceIdentity.TryFromUri(pair.Second, out var second)
                || first == second)
                throw new InvalidOperationException("The two source URIs did not produce distinct identities.");
            checksum += first.Path.Length + second.Path.Length;
            var completed = index + 1;
            if (completed % 100_000 == 0)
            {
                Console.WriteLine($"PROGRESS {completed}");
                Console.Out.Flush();
            }
        }
        Console.WriteLine($"FINISHED {iterations} {checksum}");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<TrialResult> RunTrialAsync(int index, int iterations, string inputPath, string inputHash)
    {
        var started = DateTimeOffset.UtcNow;
        using var process = new Process();
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The .NET host path is unavailable.");
        var assembly = Assembly.GetExecutingAssembly().Location;
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(assembly);
        foreach (var part in new[] { "--child", "--input", inputPath, "--expected-sha", inputHash,
                     "--iterations", iterations.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(part);
        process.StartInfo = start;
        if (!process.Start()) throw new InvalidOperationException("The child process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        var timedOut = !process.WaitForExit(TrialTimeoutMilliseconds);
        if (timedOut)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        var stdout = await output;
        var stderr = await errors;
        var lastProgress = 0;
        var finished = false;
        string? childAssemblyHash = null;
        string? exceptionType = null;
        string? exceptionHResult = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 3 && fields[0] == "START") childAssemblyHash = fields[2];
            if (fields.Length >= 2 && fields[0] == "PROGRESS" && int.TryParse(fields[1], out var progress))
                lastProgress = Math.Max(lastProgress, progress);
            if (fields.Length >= 2 && fields[0] == "FINISHED" && int.TryParse(fields[1], out var total))
            {
                lastProgress = Math.Max(lastProgress, total);
                finished = true;
            }
        }
        var unhandled = System.Text.RegularExpressions.Regex.Match(stderr,
            @"Unhandled exception\. ([A-Za-z0-9_.]+):");
        if (unhandled.Success) exceptionType = unhandled.Groups[1].Value;
        var status = timedOut ? "timed-out" : process.ExitCode == 0 && finished && lastProgress == iterations
            && childAssemblyHash == FileHash(typeof(SourceIdentity).Assembly.Location) ? "passed" : "failed";
        return new TrialResult(index, started, DateTimeOffset.UtcNow, process.Id, status,
            timedOut ? null : process.ExitCode, timedOut ? null : $"0x{unchecked((uint)process.ExitCode):X8}",
            lastProgress, childAssemblyHash, exceptionType, exceptionHResult,
            stderr.Length > 131_072 ? stderr[..131_072] : stderr, stderr.Length > 131_072);
    }

    private static UriPair ReadPair(byte[] inputBytes)
    {
        using var document = JsonDocument.Parse(inputBytes);
        var root = document.RootElement;
        if (root.TryGetProperty("firstUri", out var first))
        {
            var second = root.GetProperty("secondUri");
            var pair = new UriPair(first.GetString()!, second.GetString()!,
                root.GetProperty("kind").GetString()!, root.TryGetProperty("provenance", out var note) ? note.GetString() : null);
            if (pair.First.Length != root.GetProperty("firstUriUtf16Length").GetInt32()
                || pair.Second.Length != root.GetProperty("secondUriUtf16Length").GetInt32())
                throw new InvalidDataException("The declared URI lengths do not match the fixture.");
            return pair;
        }
        foreach (var failure in root.GetProperty("failures").EnumerateArray())
        {
            if (!failure.TryGetProperty("exception", out var exception)
                || !exception.TryGetProperty("uriIdentification", out var evidence)
                || evidence.GetProperty("status").GetString() != "available") continue;
            if (!evidence.GetProperty("uriCaptureComplete").GetBoolean()
                || !evidence.GetProperty("comparisonOtherUriCaptureComplete").GetBoolean()) continue;
            var primary = DecodeCapturedUri(evidence, "uri");
            var comparison = DecodeCapturedUri(evidence, "comparisonOtherUri");
            if (evidence.GetProperty("comparisonSide").GetString() is not ("left" or "right"))
                throw new InvalidDataException("The comparison side is unavailable.");
            return evidence.GetProperty("comparisonSide").GetString() == "right"
                ? new UriPair(primary, comparison, "source-analysis-failure-receipt", null)
                : new UriPair(comparison, primary, "source-analysis-failure-receipt", null);
        }
        throw new InvalidDataException("No complete captured URI comparison exists in the receipt.");
    }

    private static string DecodeCapturedUri(JsonElement evidence, string prefix)
    {
        var length = evidence.GetProperty(prefix + "CodeUnitLength").GetInt32();
        var captured = evidence.GetProperty(prefix + "CapturedCodeUnits").GetInt32();
        var hex = evidence.GetProperty(prefix + "Utf16Hex").GetString()!;
        if (length is < 1 or > 4096 || captured != length || hex.Length != length * 4)
            throw new InvalidDataException("The captured URI is incomplete or exceeds the supported bound.");
        var chars = new char[length];
        for (var index = 0; index < length; index++)
        {
            var digits = hex.AsSpan(index * 4, 4);
            if (!digits.ToArray().All(char.IsAsciiHexDigit))
                throw new InvalidDataException("The captured URI contains invalid UTF-16 hex.");
            chars[index] = (char)int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return new string(chars);
    }

    private static Dictionary<string, string> ParseOptions(string[] args, bool child)
    {
        if (args.Length == 0 || args.Length % 2 != 0) throw new ArgumentException("Use --input, --report, --trials and --iterations.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = child
            ? new HashSet<string>(["--input", "--expected-sha", "--iterations"], StringComparer.Ordinal)
            : new HashSet<string>(["--input", "--report", "--trials", "--iterations"], StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!allowed.Contains(args[index]) || !options.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("An option is invalid or duplicated.");
        }
        return options;
    }

    private static string Required(Dictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing {name}.");

    private static int BoundedNumber(Dictionary<string, string> options, string name, int maximum)
        => int.TryParse(Required(options, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is >= 1 && value <= maximum ? value
            : throw new ArgumentOutOfRangeException(name, $"Choose 1..{maximum}.");

    private static string PrepareReportPath(string value)
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp"));
        var path = Path.GetFullPath(value);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("The report must be under the current repository's ignored .tmp directory.");
        Directory.CreateDirectory(root);
        var cursor = root;
        var relativeDirectory = Path.GetRelativePath(root, Path.GetDirectoryName(path)!);
        foreach (var part in relativeDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A linked diagnostic output directory is not allowed.");
            cursor = Path.Combine(cursor, part);
            Directory.CreateDirectory(cursor);
        }
        if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A linked diagnostic output directory is not allowed.");
        if (File.Exists(path)) throw new IOException("The report already exists; choose a new path.");
        return path;
    }

    private static void Save(string path, Report report, bool overwrite)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(report, JsonOptions) + "\n");
            if (overwrite && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A linked diagnostic report is not allowed.");
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static object CaptureEnvironment()
    {
        var corelib = typeof(object).Assembly;
        var source = typeof(SourceIdentity).Assembly;
        var harness = Assembly.GetExecutingAssembly();
        return new
        {
            framework = RuntimeInformation.FrameworkDescription,
            runtimeVersion = Environment.Version.ToString(),
            runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            os = RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processId = Environment.ProcessId,
            hostSha256 = Environment.ProcessPath is { } host ? FileHash(host) : null,
            corelib = new { version = corelib.GetName().Version?.ToString(), sha256 = FileHash(corelib.Location) },
            sourceIdentity = new { version = source.GetName().Version?.ToString(), sha256 = FileHash(source.Location) },
            harness = new { version = harness.GetName().Version?.ToString(), sha256 = FileHash(harness.Location) }
        };
    }

    private static string FileHash(string path) => Hash(File.ReadAllBytes(path));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string HashUtf16(string value)
    {
        var bytes = new byte[value.Length * 2];
        for (var index = 0; index < value.Length; index++)
        {
            bytes[index * 2] = (byte)value[index];
            bytes[index * 2 + 1] = (byte)(value[index] >> 8);
        }
        return Hash(bytes);
    }

    private sealed record UriPair(string First, string Second, string Kind, string? Provenance);
    private sealed record InputIdentity(string Kind, string? Provenance, string FileSha256,
        int FirstUriUtf16Length, string FirstUriUtf16Sha256, int SecondUriUtf16Length, string SecondUriUtf16Sha256);
    private sealed record Invocation(int Trials, int IterationsPerTrial, int TrialTimeoutMilliseconds, string Operation);
    private sealed record TrialResult(int Index, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, int ChildPid,
        string Status, int? ExitCode, string? ExitCodeHex, int LastReportedCompleted, string? ChildSourceAssemblySha256,
        string? ExceptionType, string? ExceptionHResult, string Stderr, bool StderrTruncated);
    private sealed class Report
    {
        public string Kind { get; } = "issue-415-source-identity-uri-reproduction";
        public int SchemaVersion { get; } = 1;
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset? FinishedUtc { get; set; }
        public bool Complete { get; set; }
        public required InputIdentity Input { get; init; }
        public required Invocation Invocation { get; init; }
        public required object Environment { get; init; }
        public required List<TrialResult> Trials { get; init; }
    }
}
