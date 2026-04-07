using System.Globalization;
using System.Net;
using System.Text.Json;
using SmartmeterGateway.Output;

namespace SmartmeterGateway;

internal sealed class MeterSyncRunner(AppConfig config, List<ISeriesOutput> outputs)
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };
    private const string OriginDatabase = "origin";
    private static readonly TimeSpan SamplingInterval = TimeSpan.FromMinutes(15);

    private readonly AppConfig _config = config;
    private readonly List<ISeriesOutput> _outputs = outputs;

    public async Task RunAsync(List<MeterConfig> meters, bool enablePolling, CancellationToken ct)
    {
        var runtime = await RunInitialCatchUpAsync(meters, ct);
        if (ct.IsCancellationRequested)
        {
            Console.WriteLine("Initial catch-up canceled.");
            return;
        }

        if (!enablePolling)
        {
            return;
        }

        if (runtime.Count == 0)
        {
            Console.WriteLine("No meter runtime state available after initial catch-up. Polling will not start.");
            return;
        }

        Console.WriteLine($"Polling enabled: interval={_config.Polling!.IntervalMinutes}m offset={_config.Polling.OffsetMinutes}m");
        await RunPollingAsync(_config.Polling, runtime, ct);
    }

    private async Task<List<MeterRuntimeState>> RunInitialCatchUpAsync(List<MeterConfig> meters, CancellationToken ct)
    {
        var runtimeStates = new List<MeterRuntimeState>(meters.Count);
        foreach (var meter in meters)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var state = await InitializeMeterRuntimeAsync(meter, ct);
            if (state is not null)
            {
                runtimeStates.Add(state);
            }
        }

        return runtimeStates;
    }

    private async Task<MeterRuntimeState?> InitializeMeterRuntimeAsync(MeterConfig meter, CancellationToken ct)
    {
        var meterKey = MakeSafe(string.IsNullOrWhiteSpace(meter.Name) ? meter.BaseUrl.Host : meter.Name);
        var outDir = Path.Combine(_config.OutputRoot, meterKey);
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"=== Initial catch-up for meter {meterKey} ({meter.BaseUrl}) ===");
        try
        {
            if (meter.AllowInvalidServerCertificate)
            {
                Console.WriteLine("WARN: TLS server certificate validation is DISABLED. Do not use in production.");
            }

            using var smgw = await SmgwClient.CreateAsync(
                meter.BaseUrl,
                new NetworkCredential(meter.Username, meter.Password),
                meter.AllowInvalidServerCertificate,
                ct);

            var (usagePoints, userInfoJson) = await smgw.GetUsagePointsAsync(ct);
            var selected = SmgwClient.SelectCanonicalUsagePoints(usagePoints);
            await SaveJsonPrettyAsync(Path.Combine(outDir, "user-info.json"), userInfoJson);

            var originSeries = new List<OriginSeries>(capacity: selected.Count);
            foreach (var up in selected)
            {
                ct.ThrowIfCancellationRequested();
                var upiJson = await smgw.FetchUsagePointInfoRawAsync(up.UsagePointId, ct);
                await SaveJsonPrettyAsync(Path.Combine(outDir, $"usage-point-info-{MakeSafe(up.UsagePointName)}.json"), upiJson);
                var scalerOrNull = SmgwClient.ParseOriginScalerFromUsagePointInfoJson(upiJson);
                if (scalerOrNull is null)
                {
                    Console.WriteLine($"No origin database for {up.UsagePointName}.");
                    continue;
                }

                originSeries.Add(new OriginSeries(up, scalerOrNull.Value));
            }

            if (originSeries.Count == 0)
            {
                Console.WriteLine($"WARN meter {meterKey}: no origin series found.");
                return null;
            }

            var lastSuccessByUsagePointId = await InitialDownloadToOutputsAsync(smgw, originSeries, meterKey, outDir, ct);
            var seriesStates = originSeries
                .Select(series => new SeriesRuntimeState(series)
                {
                    LastSuccessUtc = lastSuccessByUsagePointId.GetValueOrDefault(series.UsagePoint.UsagePointId),
                })
                .ToList();

            return new MeterRuntimeState(meter, meterKey, outDir, seriesStates);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR meter {meterKey}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<Dictionary<string, DateTimeOffset?>> InitialDownloadToOutputsAsync(
        SmgwClient smgw,
        List<OriginSeries> originSeries,
        string meterKey,
        string outDir,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetFullPath(outDir));
        var lastSuccessByUsagePointId = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);

        foreach (var series in originSeries)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var up = series.UsagePoint;
            var cursors = new Dictionary<ISeriesOutput, OutputCursor?>(_outputs.Count);
            foreach (var output in _outputs)
            {
                ct.ThrowIfCancellationRequested();
                cursors[output] = await output.TryGetCursorAsync(meterKey, outDir, series);
            }

            var hasMissingCursor = cursors.Values.Any(c => c is null);
            var lastKnownTimestamps = cursors.Values
                .Where(c => c is not null)
                .Select(c => c!.TimestampUtc)
                .ToList();

            DateTimeOffset? stopBeforeUtc = null;
            if (!hasMissingCursor && lastKnownTimestamps.Count > 0)
            {
                var oldestCursorTs = lastKnownTimestamps.Min();
                stopBeforeUtc = oldestCursorTs - SamplingInterval;
            }

            Console.WriteLine(!hasMissingCursor && lastKnownTimestamps.Count > 0
                ? $"Incremental download {up.UsagePointName}: oldestCursor={FormatUtcZ(lastKnownTimestamps.Min())}..."
                : $"Backfill download {up.UsagePointName}: at least one output has no cursor...");

            var points = await smgw.DownloadUsagePointBackwardsInMemoryAsync(
                up.UsagePointId,
                OriginDatabase,
                series.Scaler,
                SamplingInterval,
                stopBeforeUtc,
                ct);

            if (points.Count == 0)
            {
                Console.WriteLine($"No origin readings found for {up.UsagePointName}.");
                lastSuccessByUsagePointId[up.UsagePointId] = lastKnownTimestamps.Count > 0 ? lastKnownTimestamps.Max() : null;
                continue;
            }

            LogGaps(OriginDatabase, SamplingInterval, points);

            foreach (var output in _outputs)
            {
                ct.ThrowIfCancellationRequested();
                var cursor = cursors[output];
                if (cursor is not null)
                {
                    LogOverlapWarning(output.Name, up.UsagePointName, points, cursor);
                }

                var pointsToWrite = cursor is null
                    ? points
                    : points.Where(p => p.TargetTimeUtc > cursor.TimestampUtc).ToList();

                if (cursor is not null && pointsToWrite.Count == 0)
                {
                    Console.WriteLine($"{output.Name}: No new points for {up.UsagePointName}.");
                    continue;
                }

                var writeResult = await output.WriteAsync(meterKey, outDir, series, pointsToWrite, cursor is not null);
                Console.WriteLine($"{output.Name}: {(cursor is not null ? "appended" : "wrote")} {writeResult.WrittenCount} rows -> {writeResult.TargetDescription}");
            }

            lastSuccessByUsagePointId[up.UsagePointId] = points[^1].TargetTimeUtc;
        }

        return lastSuccessByUsagePointId;
    }

    private async Task RunPollingAsync(PollingSettings polling, List<MeterRuntimeState> runtimeStates, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(polling.IntervalMinutes);
        var offset = TimeSpan.FromMinutes(polling.OffsetMinutes);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var nextTickUtc = GetNextAlignedTickUtc(now, interval, offset);
            var wait = nextTickUtc - now;
            if (wait > TimeSpan.Zero)
            {
                Console.WriteLine($"Next polling tick at {nextTickUtc.ToLocalTime():HH:mm} (in {wait.TotalMinutes:F1} min)");
                try
                {
                    await Task.Delay(wait, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }

            Console.WriteLine($"=== Polling cycle at {FormatUtcZ(DateTimeOffset.UtcNow)} ===");
            foreach (var meterState in runtimeStates)
            {
                await PollMeterOnceAsync(meterState);
            }
        }

        Console.WriteLine("Polling stopped.");
    }

    private async Task PollMeterOnceAsync(MeterRuntimeState meterState)
    {
        try
        {
            using var smgw = await SmgwClient.CreateAsync(
                meterState.Meter.BaseUrl,
                new NetworkCredential(meterState.Meter.Username, meterState.Meter.Password),
                meterState.Meter.AllowInvalidServerCertificate);

            foreach (var state in meterState.SeriesStates)
            {
                if (state.LastSuccessUtc is null)
                {
                    Console.WriteLine($"{meterState.MeterKey}/{state.Series.UsagePoint.UsagePointName}: no last-success cursor, skipping polling for this series.");
                    continue;
                }

                var points = await smgw.DownloadUsagePointBackwardsInMemoryAsync(
                    state.Series.UsagePoint.UsagePointId,
                    OriginDatabase,
                    state.Series.Scaler,
                    SamplingInterval,
                    state.LastSuccessUtc.Value);

                var pointsToWrite = points.Where(p => p.TargetTimeUtc > state.LastSuccessUtc.Value).ToList();
                if (pointsToWrite.Count == 0)
                {
                    Console.WriteLine($"{meterState.MeterKey}/{state.Series.UsagePoint.UsagePointName}: no new points since {FormatUtcZ(state.LastSuccessUtc.Value)}");
                    continue;
                }

                var writeSucceeded = true;
                foreach (var output in _outputs)
                {
                    try
                    {
                        var result = await output.WriteAsync(meterState.MeterKey, meterState.OutDir, state.Series, pointsToWrite, append: true);
                        Console.WriteLine($"{output.Name}: appended {result.WrittenCount} rows -> {result.TargetDescription}");
                    }
                    catch (Exception ex)
                    {
                        writeSucceeded = false;
                        Console.Error.WriteLine($"ERROR {meterState.MeterKey}/{state.Series.UsagePoint.UsagePointName} output={output.Name}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (!writeSucceeded)
                {
                    Console.Error.WriteLine($"WARN {meterState.MeterKey}/{state.Series.UsagePoint.UsagePointName}: cursor not advanced due to write errors.");
                    continue;
                }

                state.LastSuccessUtc = pointsToWrite[^1].TargetTimeUtc;
                Console.WriteLine($"Cursor advanced {meterState.MeterKey}/{state.Series.UsagePoint.UsagePointName} -> {FormatUtcZ(state.LastSuccessUtc.Value)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR polling meter {meterState.MeterKey}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static DateTimeOffset GetNextAlignedTickUtc(DateTimeOffset nowUtc, TimeSpan interval, TimeSpan offset)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Polling interval must be > 0.");
        }

        if (offset < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Polling offset must be >= 0.");
        }

        var nowTicks = nowUtc.UtcTicks;
        var baseTicks = nowTicks - offset.Ticks;
        var nextSlotTicks = ((baseTicks / interval.Ticks) + 1) * interval.Ticks;
        var nextTickTicks = nextSlotTicks + offset.Ticks;
        return new DateTimeOffset(nextTickTicks, TimeSpan.Zero);
    }

    private static void LogOverlapWarning(string outputName, string usagePointName, List<ReadingPoint> points, OutputCursor cursor)
    {
        var overlapPoint = points.FirstOrDefault(p => p.TargetTimeUtc == cursor.TimestampUtc);
        if (overlapPoint is null)
            Console.WriteLine($"WARN {outputName} overlap missing for {usagePointName}: did not receive point at {FormatUtcZ(cursor.TimestampUtc)}");
        else if (cursor.Value is null)
            Console.WriteLine($"WARN {outputName} overlap compare skipped for {usagePointName}: last value is empty");
        else if (overlapPoint.Value is null)
            Console.WriteLine($"WARN {outputName} overlap compare skipped for {usagePointName}: gateway value is empty");
        else if (overlapPoint.Value.Value != cursor.Value.Value)
            Console.WriteLine($"WARN {outputName} overlap differs for {usagePointName} at {FormatUtcZ(cursor.TimestampUtc)}: sink={cursor.Value} gw={overlapPoint.Value}");
    }

    private static void LogGaps(string database, TimeSpan samplingInterval, List<ReadingPoint> points)
    {
        if (points.Count < 2)
        {
            Console.WriteLine($"{database}: points={points.Count}");
            return;
        }

        var gapEvents = 0;
        long missingSlots = 0;
        var irregularDeltas = 0;
        var maxGap = TimeSpan.Zero;

        for (var i = 1; i < points.Count; i++)
        {
            var prev = points[i - 1].TargetTimeUtc;
            var cur = points[i].TargetTimeUtc;
            var delta = cur - prev;
            if (delta <= samplingInterval) continue;

            gapEvents++;
            if (delta > maxGap) maxGap = delta;

            var wholeSteps = delta.Ticks / samplingInterval.Ticks;
            if (wholeSteps > 0)
            {
                missingSlots += Math.Max(0, wholeSteps - 1);
            }
            if (delta.Ticks % samplingInterval.Ticks != 0)
            {
                irregularDeltas++;
            }
        }

        var rangeFrom = FormatUtcZ(points[0].TargetTimeUtc);
        var rangeTo = FormatUtcZ(points[^1].TargetTimeUtc);
        Console.WriteLine(
            $"{database}: points={points.Count}, range={rangeFrom}..{rangeTo}, " +
            $"gapEvents={gapEvents}, missingSlots={missingSlots}, irregularDeltas={irregularDeltas}, maxGap={maxGap}");
    }

    private static string FormatUtcZ(DateTimeOffset ts)
        => ts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

    private static async Task SaveJsonPrettyAsync(string path, byte[] json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pretty = JsonSerializer.Serialize(doc.RootElement, PrettyJsonOptions);
            await File.WriteAllTextAsync(path, pretty);
        }
        catch (JsonException)
        {
            var rawPath = Path.ChangeExtension(path, ".response.bin");
            await File.WriteAllBytesAsync(rawPath, json);
            Console.WriteLine($"WARN: Response was not valid JSON. Raw bytes written: {Path.GetFullPath(rawPath)}");
        }
    }

    private static string MakeSafe(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var len = 0;
        foreach (var ch in value)
        {
            buffer[len++] = char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_';
        }
        return new string(buffer[..len]);
    }
}

internal sealed class SeriesRuntimeState(OriginSeries series)
{
    public OriginSeries Series { get; } = series;
    public DateTimeOffset? LastSuccessUtc { get; set; }
}

internal sealed class MeterRuntimeState(MeterConfig meter, string meterKey, string outDir, List<SeriesRuntimeState> seriesStates)
{
    public MeterConfig Meter { get; } = meter;
    public string MeterKey { get; } = meterKey;
    public string OutDir { get; } = outDir;
    public List<SeriesRuntimeState> SeriesStates { get; } = seriesStates;
}
