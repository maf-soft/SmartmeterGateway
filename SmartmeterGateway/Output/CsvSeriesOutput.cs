using System.Globalization;
using System.Text;

namespace SmartmeterGateway.Output;

internal sealed class CsvSeriesOutput : ISeriesOutput
{
    public string Name => "csv";

    public CsvSeriesOutput(CsvTarget options) => _ = options;

    public void Dispose()
    {
    }

    public Task<OutputCursor?> TryGetCursorAsync(string meterKey, string outDir, OriginSeries series)
    {
        var csvPath = GetCsvPath(outDir, series.UsagePoint);
        if (!File.Exists(csvPath)) return Task.FromResult<OutputCursor?>(null);

        var lastLine = ReadLastNonEmptyLine(csvPath);
        if (lastLine is null || lastLine.StartsWith("TargetTimeUtc", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<OutputCursor?>(null);
        }

        var comma = lastLine.IndexOf(',');
        if (comma <= 0)
        {
            throw new InvalidOperationException($"CSV last line is not 'ts,value': {lastLine}");
        }

        var tsText = lastLine[..comma].Trim();
        var valText = lastLine[(comma + 1)..].Trim();
        if (!DateTimeOffset.TryParse(tsText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
        {
            throw new InvalidOperationException($"CSV timestamp parse failed: '{tsText}'");
        }

        decimal? parsedValue = null;
        if (!string.IsNullOrWhiteSpace(valText))
        {
            if (!decimal.TryParse(valText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException($"CSV value parse failed: '{valText}'");
            }

            parsedValue = value;
        }

        return Task.FromResult<OutputCursor?>(new OutputCursor(ts.ToUniversalTime(), parsedValue));
    }

    public async Task<OutputWriteResult> WriteAsync(string meterKey, string outDir, OriginSeries series, IReadOnlyList<ReadingPoint> points, bool append)
    {
        var csvPath = GetCsvPath(outDir, series.UsagePoint);
        await using var writer = new StreamWriter(csvPath, append: append);
        if (!append)
        {
            await writer.WriteLineAsync("TargetTimeUtc,Value");
        }

        foreach (var p in points.OrderBy(p => p.TargetTimeUtc))
        {
            var ts = p.TargetTimeUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);
            var v = p.Value is null ? "" : p.Value.Value.ToString(CultureInfo.InvariantCulture);
            await writer.WriteLineAsync($"{ts},{v}");
        }

        await writer.FlushAsync();
        return new OutputWriteResult(points.Count, Path.GetFullPath(csvPath));
    }

    private static string GetCsvPath(string outDir, UsagePoint usagePoint)
    {
        var direction = usagePoint.UsagePointName[^5..].ToLowerInvariant();
        return Path.Combine(outDir, $"readings-15min-{direction}.csv");
    }

    private static string? ReadLastNonEmptyLine(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var n = (int)Math.Min(64, fs.Length);
        if (n == 0) return null;
        fs.Seek(-n, SeekOrigin.End);
        var buf = new byte[n];
        var read = fs.Read(buf, 0, n);
        var s = Encoding.UTF8.GetString(buf, 0, read).TrimEnd();
        if (string.IsNullOrWhiteSpace(s)) return null;
        var i = s.LastIndexOf('\n');
        return s[(i + 1)..].TrimEnd('\r');
    }
}
