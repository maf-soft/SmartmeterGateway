using System.Text.Json;
using SmartmeterGateway.Output;

namespace SmartmeterGateway;

internal static class Program
{
	private static readonly JsonSerializerOptions ConfigJsonOptions = new() { PropertyNameCaseInsensitive = true };

	public static async Task<int> Main(string[] args)
	{
		var config = LoadConfig();
		using var outputs = CreateOutputs(config);

		var meters = config.Meters.Where(m => m.Active).ToList();
		if (meters.Count == 0)
		{
			throw new InvalidOperationException("No active meters configured. Set Meters[].Active=true in SmartmeterGateway/appsettings.json");
		}

		var enablePolling = HasPollFlag(args);
		using var cts = new CancellationTokenSource();
		if (enablePolling)
		{
			Console.CancelKeyPress += (_, e) =>
			{
				e.Cancel = true;
				cts.Cancel();
				Console.WriteLine("Ctrl+C received. Stopping polling...");
			};
		}

		var runner = new MeterSyncRunner(config, outputs.Items);
		await runner.RunAsync(meters, enablePolling, cts.Token);
		return 0;
	}

	static bool HasPollFlag(string[] args) =>
		args.Any(a => string.Equals(a, "--poll", StringComparison.OrdinalIgnoreCase));

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

		var polling = NormalizePolling(cfg.Polling);
		return cfg with { OutputRoot = outputRoot, Meters = meters, Outputs = outputs, Polling = polling };
	}

	static PollingSettings NormalizePolling(PollingSettings? pollingOrNull)
	{
		var polling = pollingOrNull ?? new PollingSettings();

		if (polling.IntervalMinutes <= 0)
		{
			throw new InvalidOperationException("Polling.IntervalMinutes must be > 0.");
		}

		if (polling.OffsetMinutes < 0)
		{
			throw new InvalidOperationException("Polling.OffsetMinutes must be >= 0.");
		}

		return polling;
	}

	static DisposableBag<ISeriesOutput> CreateOutputs(AppConfig config)
	{
		var outputs = ((IOutputTarget[])[config.Outputs.Csv, config.Outputs.InfluxDb])
			.Where(t => t.Enabled)
			.Select(t => t.CreateOutput())
			.ToList();

		return new(outputs);
	}
}

sealed class DisposableBag<T>(List<T> items) : IDisposable where T : IDisposable
{
	public List<T> Items { get; } = items;

	public void Dispose()
	{
		foreach (var item in Items)
		{
			item.Dispose();
		}
	}
}

sealed record AppConfig(string OutputRoot, List<MeterConfig> Meters, OutputTargets Outputs, PollingSettings? Polling);

sealed record PollingSettings(int IntervalMinutes = 15, int OffsetMinutes = 1);

sealed record MeterConfig(
	string Name,
	Uri BaseUrl,
	string Username,
	string Password,
	bool Active,
	bool AllowInvalidServerCertificate);
