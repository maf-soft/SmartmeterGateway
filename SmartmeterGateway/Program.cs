using System.Globalization;
using System.Net;
using System.Text.Json;
using SmartmeterGateway.Output;

namespace SmartmeterGateway;

internal static class Program
{
	private static readonly JsonSerializerOptions ConfigJsonOptions = new() { PropertyNameCaseInsensitive = true };
	private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };
	private const string OriginDatabase = "origin";

	// Minimal console tool: download all available 15-minute readings to CSV (Bezug + Einspeisung)
	// Supports multiple meters via appsettings.json with an Active flag.
	public static async Task<int> Main()
	{
		var config = LoadConfig();
		using var outputs = CreateOutputs(config);
		var meters = config.Meters.Where(m => m.Active).ToList();
		if (meters.Count == 0)
		{
			throw new InvalidOperationException("No active meters configured. Set Meters[].Active=true in SmartmeterGateway/appsettings.json");
		}

		foreach (var meter in meters)
		{
			await ProcessMeterAsync(config, meter, outputs.Items);
		}

		return 0;
	}

	static AppConfig LoadConfig()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
		if (!File.Exists(path))
		{
			throw new InvalidOperationException($"Config file not found: {path}");
		}

		var json = File.ReadAllText(path);
		var cfg = JsonSerializer.Deserialize<AppConfig>(json, ConfigJsonOptions)
			?? throw new InvalidOperationException($"Config could not be parsed: {path}");
		var outputRoot = string.IsNullOrWhiteSpace(cfg.OutputRoot) ? "output" : cfg.OutputRoot;
		var meters = (cfg.Meters ?? [])
			.Where(m => m.BaseUrl is not null && !string.IsNullOrWhiteSpace(m.Username) && !string.IsNullOrWhiteSpace(m.Password))
			.ToList();

		var outputs = cfg.Outputs ?? new OutputTargets(new CsvTarget(true), new InfluxDbTarget(false));
		if (!outputs.Csv.Enabled && !outputs.InfluxDb.Enabled)
		{
			throw new InvalidOperationException("At least one output must be enabled: Outputs.Csv.Enabled and/or Outputs.InfluxDb.Enabled.");
		}

		return cfg with { OutputRoot = outputRoot, Meters = meters, Outputs = outputs };
	}

	static DisposableBag<ISeriesOutput> CreateOutputs(AppConfig config)
	{
		var outputs = ((IOutputTarget[])[config.Outputs.Csv, config.Outputs.InfluxDb])
			.Where(t => t.Enabled)
			.Select(t => t.CreateOutput())
			.ToList();

		return new(outputs);
	}

	static async Task ProcessMeterAsync(AppConfig config, MeterConfig meter, List<ISeriesOutput> outputs)
	{
		var meterKey = MakeSafe(string.IsNullOrWhiteSpace(meter.Name) ? meter.BaseUrl.Host : meter.Name);
		var outDir = Path.Combine(config.OutputRoot, meterKey);
		Directory.CreateDirectory(outDir);

		Console.WriteLine($"=== Meter {meterKey} ({meter.BaseUrl}) ===");
		try
		{
			if (meter.AllowInvalidServerCertificate)
			{
				Console.WriteLine("WARN: TLS server certificate validation is DISABLED. Do not use in production.");
			}

			using var smgw = await SmgwClient.CreateAsync(
				meter.BaseUrl,
				new NetworkCredential(meter.Username, meter.Password),
				meter.AllowInvalidServerCertificate);
			var (usagePoints, userInfoJson) = await smgw.GetUsagePointsAsync();

			var selected = SmgwClient.SelectCanonicalUsagePoints(usagePoints);
			await SaveJsonPrettyAsync(Path.Combine(outDir, "user-info.json"), userInfoJson);

			var originSeries = new List<OriginSeries>(capacity: selected.Count);
			foreach (var up in selected)
			{
				var upiJson = await smgw.FetchUsagePointInfoRawAsync(up.UsagePointId);
				await SaveJsonPrettyAsync(Path.Combine(outDir, $"usage-point-info-{MakeSafe(up.UsagePointName)}.json"), upiJson);
				var scalerOrNull = SmgwClient.ParseOriginScalerFromUsagePointInfoJson(upiJson);
				if (scalerOrNull is null)
				{
					Console.WriteLine($"No origin database for {up.UsagePointName}.");
					continue;
				}

				originSeries.Add(new OriginSeries(up, scalerOrNull.Value));
			}

			await DownloadReadingsToOutputsAsync(smgw, originSeries, meterKey, outDir, outputs);
		}
		catch (Exception ex)
		{
			var msg = $"ERROR meter {meterKey}: {ex.GetType().Name}: {ex.Message}";
			Console.Error.WriteLine(msg);
		}
	}

	static async Task DownloadReadingsToOutputsAsync(SmgwClient smgw, List<OriginSeries> originSeries, string meterKey, string outDir, List<ISeriesOutput> outputs)
	{
		Directory.CreateDirectory(Path.GetFullPath(outDir));

		foreach (var series in originSeries)
		{
			var up = series.UsagePoint;
			var scaler = series.Scaler;

			var samplingInterval = TimeSpan.FromMinutes(15);
			var cursors = new Dictionary<ISeriesOutput, OutputCursor?>(outputs.Count);
			foreach (var output in outputs)
			{
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
				stopBeforeUtc = oldestCursorTs - samplingInterval;
			}

			Console.WriteLine(!hasMissingCursor && lastKnownTimestamps.Count > 0
				? $"Incremental download {up.UsagePointName}: oldestCursor={FormatUtcZ(lastKnownTimestamps.Min())}..."
				: $"Backfill download {up.UsagePointName}: at least one output has no cursor...");

			var points = await smgw.DownloadUsagePointBackwardsInMemoryAsync(
				up.UsagePointId,
				OriginDatabase,
				scaler,
				samplingInterval,
				stopBeforeUtc);
			if (points.Count == 0)
			{
				Console.WriteLine($"No origin readings found for {up.UsagePointName}.");
				continue;
			}

			LogGaps(OriginDatabase, samplingInterval, points);

			foreach (var output in outputs)
			{
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
		}
	}

	static void LogOverlapWarning(string outputName, string usagePointName, List<ReadingPoint> points, OutputCursor cursor)
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

	static void LogGaps(string database, TimeSpan samplingInterval, List<ReadingPoint> points)
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

	static string FormatUtcZ(DateTimeOffset ts)
		=> ts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

	static async Task SaveJsonPrettyAsync(string path, byte[] json)
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

	static string MakeSafe(string value)
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

sealed class DisposableBag<T>(List<T> items) : IDisposable where T : IDisposable
{
	public List<T> Items { get; } = items;

	public void Dispose()
	{
		foreach (var item in Items) item.Dispose();
	}
}

sealed record AppConfig(string OutputRoot, List<MeterConfig> Meters, OutputTargets Outputs);

sealed record OutputTargets(CsvTarget Csv, InfluxDbTarget InfluxDb);

interface IOutputTarget
{
	bool Enabled { get; }
	ISeriesOutput CreateOutput();
}

sealed record CsvTarget(bool Enabled) : IOutputTarget
{
	public ISeriesOutput CreateOutput() => new CsvSeriesOutput(this);
}

sealed record InfluxDbTarget(
	bool Enabled,
	string Url = "",
	string Org = "",
	string Bucket = "",
	string Token = "",
	string Measurement = "",
	bool AllowInvalidServerCertificate = false) : IOutputTarget
{
	public ISeriesOutput CreateOutput() => new InfluxDbSeriesOutput(this);
}

sealed record MeterConfig(
	string Name,
	Uri BaseUrl,
	string Username,
	string Password,
	bool Active,
	bool AllowInvalidServerCertificate);
