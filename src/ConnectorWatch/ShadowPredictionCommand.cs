using System.Globalization;
using System.Text;

namespace ConnectorWatch;

/// <summary>An offline entry point, dispatched before configuration or native initialization.</summary>
public static class ShadowPredictionCommand
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains("--evaluate-prediction", StringComparer.Ordinal)) return false;
        string directory = Path.GetFullPath(RequiredValue(args, "--evaluate-prediction"));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        string? output = OptionalValue(args, "--output");
        string? cutoff = OptionalValue(args, "--as-of");
        var asOf = DateTimeOffset.UtcNow;
        if (cutoff is not null)
        {
            if (!DateOnly.TryParseExact(cutoff, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day))
                throw new ArgumentException("--as-of must be a UTC date in YYYY-MM-DD format.");
            asOf = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            if (asOf > DateTimeOffset.UtcNow)
                throw new ArgumentException("--as-of cannot be in the future.");
        }
        if (output is not null)
        {
            output = Path.GetFullPath(output);
            if (!Path.GetExtension(output).Equals(".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--output must name a .json report.");
            if (IsWithin(output, directory))
                throw new ArgumentException("Reports must be outside the telemetry directory to preserve source data.");
            if (File.Exists(output) || File.Exists(Path.ChangeExtension(output, ".md")))
                throw new IOException("A report already exists at --output. Choose a new report name to retain history.");
        }

        var input = ShadowTelemetryReader.ReadDirectory(directory, new ShadowReadOptions { InputCutoffUtc = asOf });
        var report = ShadowPredictionEvaluation.Evaluate(input);
        string json = report.ToJson();
        if (output is null) Console.WriteLine(json);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            WriteNew(output, json);
            WriteNew(Path.ChangeExtension(output, ".md"), report.ToMarkdown());
            Console.WriteLine(output);
            Console.WriteLine(Path.ChangeExtension(output, ".md"));
        }
        return true;
    }

    static string RequiredValue(string[] args, string option) => OptionalValue(args, option)
        ?? throw new ArgumentException($"{option} requires a value.");

    static string? OptionalValue(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (Array.LastIndexOf(args, option) != index)
            throw new ArgumentException($"{option} may only be supplied once.");
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) ||
            args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{option} requires a value.");
        return args[index + 1];
    }

    static bool IsWithin(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    static void WriteNew(string path, string contents)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
