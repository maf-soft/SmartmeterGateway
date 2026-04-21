using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SmartmeterGateway.Output;

internal sealed class SqliteSeriesOutput(SqliteTarget options) : ISeriesOutput
{
    public string Name => "sqlite";
    private const int SchemaVersion = 1;

    private readonly SqliteTarget _options = options;

    public void Dispose()
    {
    }

    public static async Task ImportCsvAsync(SqliteTarget options, string outputRoot, string meterKey, string seriesName, string csvPath)
    {
        var points = File.ReadLines(csvPath)
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(',', 2))
            .Select(parts => new ReadingPoint(
                DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
                decimal.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture)))
            .OrderBy(p => p.TargetTimeUtc)
            .ToList();

        var outDir = Path.Combine(Path.GetFullPath(outputRoot), meterKey);
        Directory.CreateDirectory(outDir);

        using var sqlite = new SqliteSeriesOutput(options);
        var series = new OriginSeries(new UsagePoint(seriesName.ToUpperInvariant(), "import", "import"), 0);
        await sqlite.WriteAsync(meterKey, outDir, series, points, append: true);
    }

    public async Task<OutputCursor?> TryGetCursorAsync(string meterKey, string outDir, OriginSeries series)
    {
        var seriesName = series.UsagePoint.UsagePointName[^5..].ToUpperInvariant();
        var dbPath = ResolveDatabasePath(outDir);

        if (!File.Exists(dbPath))
        {
            return null;
        }

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await EnsureSchemaVersionAsync(conn, allowInitialize: false);

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT target_time_utc, value
FROM raw_readings
WHERE source_type = 'smartmeter'
    AND source_id = $source_id
    AND series = $series
ORDER BY target_time_utc DESC
LIMIT 1;";
        cmd.Parameters.AddWithValue("$source_id", meterKey);
        cmd.Parameters.AddWithValue("$series", seriesName);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var tsText = reader.GetString(0);
        if (!DateTimeOffset.TryParse(tsText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
        {
            throw new InvalidOperationException($"SQLite timestamp parse failed: '{tsText}'");
        }

        var value = (decimal)reader.GetDouble(1);
        return new OutputCursor(ts.ToUniversalTime(), value);
    }

    public async Task<OutputWriteResult> WriteAsync(string meterKey, string outDir, OriginSeries series, IReadOnlyList<ReadingPoint> points, bool append)
    {
        _ = append;
        if (points.Count == 0)
        {
            return new OutputWriteResult(0, "no-op");
        }

        var seriesName = series.UsagePoint.UsagePointName[^5..].ToUpperInvariant();
        var dbPath = ResolveDatabasePath(outDir);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await EnsureSchemaAsync(conn);

        await using var tx = conn.BeginTransaction();
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO raw_readings(target_time_utc, source_type, source_id, series, value_type, unit, value)
VALUES ($time, 'smartmeter', $source_id, $series, 'counter', 'Wh', $value)
ON CONFLICT(target_time_utc, source_type, source_id, series)
DO UPDATE SET
    value_type = excluded.value_type,
    unit = excluded.unit,
    value = excluded.value;";

        var pSourceId = cmd.Parameters.Add("$source_id", SqliteType.Text);
        var pSeries = cmd.Parameters.Add("$series", SqliteType.Text);
        var pTime = cmd.Parameters.Add("$time", SqliteType.Text);
        var pValue = cmd.Parameters.Add("$value", SqliteType.Real);

        var written = 0;
        foreach (var p in points.OrderBy(p => p.TargetTimeUtc))
        {
            if (p.Value is null)
            {
                throw new InvalidOperationException($"SQLite write blocked: empty value at {p.TargetTimeUtc:yyyy-MM-dd'T'HH:mm:ss'Z'} ({meterKey}/{seriesName}).");
            }

            pSourceId.Value = meterKey;
            pSeries.Value = seriesName;
            pTime.Value = p.TargetTimeUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            pValue.Value = p.Value.Value;

            await cmd.ExecuteNonQueryAsync();
            written++;
        }

        await tx.CommitAsync();
        return new OutputWriteResult(written, Path.GetFullPath(dbPath));
    }

    private async Task EnsureSchemaAsync(SqliteConnection conn)
    {
        if (await EnsureSchemaVersionAsync(conn, allowInitialize: true))
        {
            return;
        }

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS raw_readings (
    target_time_utc TEXT NOT NULL,
    source_type TEXT NOT NULL,
    source_id TEXT NOT NULL,
    series TEXT NOT NULL,
    value_type TEXT NOT NULL,
    unit TEXT NOT NULL,
    value REAL NOT NULL,
    PRIMARY KEY (target_time_utc, source_type, source_id, series)
);

CREATE INDEX IF NOT EXISTS idx_raw_readings_source_latest
ON raw_readings (source_type, source_id, series, target_time_utc DESC);
";

        await cmd.ExecuteNonQueryAsync();

        var setVersion = conn.CreateCommand();
        setVersion.CommandText = $"PRAGMA user_version = {SchemaVersion};";
        await setVersion.ExecuteNonQueryAsync();
    }

    private static async Task<bool> EnsureSchemaVersionAsync(SqliteConnection conn, bool allowInitialize)
    {
        var getVersion = conn.CreateCommand();
        getVersion.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt32(await getVersion.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        if (current == SchemaVersion)
        {
            return true;
        }

        if (current == 0 && allowInitialize)
        {
            return false;
        }

        throw new InvalidOperationException(
            $"SQLite schema version mismatch: expected {SchemaVersion}, found {current}. Recreate the SQLite file or migrate it.");
    }

    private string ResolveDatabasePath(string outDir)
    {
        if (Path.IsPathRooted(_options.DatabasePath))
        {
            return _options.DatabasePath;
        }

        var outputRoot = Directory.GetParent(Path.GetFullPath(outDir))?.FullName ?? outDir;
        return Path.Combine(outputRoot, _options.DatabasePath);
    }
}
