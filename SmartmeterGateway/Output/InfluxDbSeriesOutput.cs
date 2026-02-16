using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SmartmeterGateway.Output;

internal sealed class InfluxDbSeriesOutput : ISeriesOutput, IDisposable
{
    private const string DefaultMeasurement = "smartmeter_readings";

    private readonly InfluxDbTarget _options;
    private readonly HttpClient _http;

    public InfluxDbSeriesOutput(InfluxDbTarget options)
    {
        _options = options;
        if (string.IsNullOrWhiteSpace(_options.Url)
            || string.IsNullOrWhiteSpace(_options.Org)
            || string.IsNullOrWhiteSpace(_options.Bucket)
            || string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("InfluxDb output enabled but Url/Org/Bucket/Token are missing.");
        }

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        if (_options.AllowInvalidServerCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_options.Url, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", _options.Token);
    }

    public string Name => "influxdb";

    public async Task<OutputCursor?> TryGetCursorAsync(string meterKey, string outDir, OriginSeries series)
    {
        _ = outDir;
        var direction = series.UsagePoint.UsagePointName[^5..].ToUpperInvariant();
        var measurement = GetMeasurement();

        var sql = $"SELECT time AS max_time, value " +
                  $"FROM {EscapeIdentifier(measurement)} " +
                  $"WHERE meter = '{EscapeSqlString(meterKey)}' " +
                  $"AND direction = '{EscapeSqlString(direction)}' " +
              $"AND \"database\" = 'origin' " +
              $"ORDER BY time DESC LIMIT 1";

        var body = JsonSerializer.Serialize(new
        {
            db = _options.Bucket,
            q = sql,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/query_sql")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.InternalServerError
                && payload.Contains("table 'public.iox.", StringComparison.OrdinalIgnoreCase)
                && payload.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            throw new InvalidOperationException($"InfluxDB query failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {payload}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var row = doc.RootElement[0];
        if (!row.TryGetProperty("max_time", out var maxTimeProp) || maxTimeProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var text = maxTimeProp.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
        {
            throw new InvalidOperationException($"Invalid InfluxDB max_time timestamp: '{text}'");
        }

        decimal? value = null;
        if (row.TryGetProperty("value", out var valueProp) && valueProp.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            var valueText = valueProp.ValueKind == JsonValueKind.Number
                ? valueProp.GetRawText()
                : valueProp.GetString();

            if (!string.IsNullOrWhiteSpace(valueText)
                && decimal.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
            }
        }

        return new OutputCursor(ts.ToUniversalTime(), value);
    }

    public async Task<OutputWriteResult> WriteAsync(string meterKey, string outDir, OriginSeries series, IReadOnlyList<ReadingPoint> points, bool append)
    {
        if (points.Count == 0)
        {
            return new OutputWriteResult(0, "no-op");
        }

        var direction = series.UsagePoint.UsagePointName[^5..].ToUpperInvariant();
        var measurement = GetMeasurement();

        var body = new StringBuilder(capacity: points.Count * 64);
        var written = 0;
        foreach (var p in points.OrderBy(p => p.TargetTimeUtc))
        {
            if (p.Value is null)
            {
                continue;
            }

            var ts = p.TargetTimeUtc.ToUnixTimeSeconds();
            body.Append(EscapeMeasurement(measurement));
            body.Append(",meter=").Append(EscapeTag(meterKey));
            body.Append(",direction=").Append(EscapeTag(direction));
            body.Append(",database=origin value=").Append(p.Value.Value.ToString(CultureInfo.InvariantCulture));
            body.Append(' ').Append(ts);
            body.Append('\n');
            written++;
        }

        if (written > 0)
        {
            var endpoint = $"/api/v2/write?org={Uri.EscapeDataString(_options.Org)}&bucket={Uri.EscapeDataString(_options.Bucket)}&precision=s";
            using var content = new StringContent(body.ToString(), Encoding.UTF8, "text/plain");
            using var response = await _http.PostAsync(endpoint, content);
            if (!response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"InfluxDB write failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {payload}");
            }
        }

        return new OutputWriteResult(written, $"{_options.Url} (bucket={_options.Bucket}, measurement={measurement})");
    }

    public void Dispose() => _http.Dispose();

    private string GetMeasurement() =>
        string.IsNullOrWhiteSpace(_options.Measurement) ? DefaultMeasurement : _options.Measurement;

    private static string EscapeIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string EscapeSqlString(string value) => value
        .Replace("'", "''", StringComparison.Ordinal);

    private static string EscapeMeasurement(string value) => value
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(" ", "\\ ", StringComparison.Ordinal);

    private static string EscapeTag(string value) => value
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("=", "\\=", StringComparison.Ordinal)
        .Replace(" ", "\\ ", StringComparison.Ordinal);
}
