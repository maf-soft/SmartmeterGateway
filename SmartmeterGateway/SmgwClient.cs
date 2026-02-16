using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace SmartmeterGateway;

public sealed class SmgwClient(HttpClient http, string location) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public static async Task<SmgwClient> CreateAsync(Uri baseUrl, NetworkCredential credential, bool ignoreInvalidTlsCertificate)
    {
        var http = CreateHttpClient(baseUrl, credential, ignoreInvalidTlsCertificate);
        try
        {
            var location = await GetM2mLocationAsync(http);
            return new SmgwClient(http, location);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    public async Task<(List<UsagePoint> UsagePoints, byte[] UserInfoJson)> GetUsagePointsAsync()
    {
        var payload = Payload(("method", "user-info"));
        var json = await PostJsonAsync(payload);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("user-info", out var userInfo))
            {
                return ([], json);
            }
            if (!userInfo.TryGetProperty("usage-points", out var ups) || ups.ValueKind != JsonValueKind.Array)
            {
                return ([], json);
            }

            var results = new List<UsagePoint>();
            foreach (var up in ups.EnumerateArray())
            {
                var name = up.GetPropertyOrNull("usage-point-name");
                var taf = up.GetPropertyOrNull("taf-number");
                var id = up.GetPropertyOrNull("usage-point-id");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(taf) || string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                results.Add(new UsagePoint(name!, taf!, id!));
            }

            return (results, json);
        }
        catch (JsonException)
        {
            return ([], json);
        }
    }

    public static List<UsagePoint> SelectCanonicalUsagePoints(List<UsagePoint> all) =>
        // We only need 2 series: Bezug + Einspeisung.
        // Prefer TAF1 for each direction; otherwise fallback to TAF7.
        all
            .Where(p => p.TafNumber is "1" or "7")
            .GroupBy(p => p.UsagePointName[^5..])
            .Where(g => g.Key is "BEZUG" or "EINSP")
            .Select(g => g.OrderBy(x => x.TafNumber == "1" ? 0 : 1).First())
            .ToList()
            switch
        {
            { Count: 2 } list => list,
            _ => throw new InvalidOperationException("Could not find both BEZUG and EINSP usage points in user-info."),
        };

    public async Task<byte[]> FetchUsagePointInfoRawAsync(string usagePointId) =>
        await PostJsonAsync(Payload(
            ("method", "usage-point-info"),
            ("usage-point-id", usagePointId)));

    public static int? ParseOriginScalerFromUsagePointInfoJson(byte[] usagePointInfoJson)
    {
        using var doc = JsonDocument.Parse(usagePointInfoJson);
        if (!doc.RootElement.TryGetProperty("usage-point-info", out var upi)) return null;
        if (!upi.TryGetProperty("databases", out var dbs) || dbs.ValueKind != JsonValueKind.Array) return null;

        foreach (var db in dbs.EnumerateArray())
        {
            var name = db.GetPropertyOrNull("database");
            if (!string.Equals(name, "origin", StringComparison.OrdinalIgnoreCase)) continue;
            if (!db.TryGetProperty("channels", out var ch) || ch.ValueKind != JsonValueKind.Array) return 0;

            foreach (var c in ch.EnumerateArray())
            {
                var scalerText = c.GetPropertyOrNull("scaler");
                return int.TryParse(scalerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            }

            return 0;
        }

        return null;
    }

    public async Task<List<ReadingPoint>> DownloadUsagePointBackwardsInMemoryAsync(
        string usagePointId,
        string database,
        int scaler,
        TimeSpan samplingInterval,
        DateTimeOffset? stopBeforeUtc = null)
    {
        var pointsByTime = new Dictionary<DateTimeOffset, decimal?>(capacity: 4096);
        var windowEnd = DateTimeOffset.UtcNow;
        var windowSpan = TimeSpan.FromDays(31);
        var stepBack = windowSpan - samplingInterval;
        if (stepBack <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Invalid overlap/window configuration.");
        }

        for (; ; )
        {
            var fromUtc = windowEnd - windowSpan;
            var toUtc = windowEnd;
            if (stopBeforeUtc is not null && fromUtc < stopBeforeUtc.Value)
            {
                fromUtc = stopBeforeUtc.Value;
            }

            var rows = await FetchReadingsWindowAsync(usagePointId, database, scaler, fromUtc, toUtc);
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var r in rows)
            {
                pointsByTime.TryAdd(r.TargetTimeUtc, r.Value);
            }

            if (stopBeforeUtc is not null && fromUtc == stopBeforeUtc.Value)
            {
                break;
            }

            windowEnd -= stepBack;
        }

        return pointsByTime
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new ReadingPoint(kvp.Key, kvp.Value))
            .ToList();
    }

    public void Dispose() => http.Dispose();

    private static HttpClient CreateHttpClient(Uri baseUrl, NetworkCredential credential, bool ignoreInvalidTlsCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            Credentials = credential,
            PreAuthenticate = false,
        };

        if (ignoreInvalidTlsCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler)
        {
            BaseAddress = baseUrl,
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    private static async Task<string> GetM2mLocationAsync(HttpClient http)
    {
        using var res = await http.GetAsync("smgw/m2m", HttpCompletionOption.ResponseHeadersRead);
        if (res.StatusCode != HttpStatusCode.TemporaryRedirect || res.Headers.Location is null)
        {
            throw new InvalidOperationException($"Expected 307 + Location from GET /smgw/m2m, got {(int)res.StatusCode} {res.ReasonPhrase}");
        }

        return res.Headers.Location.ToString();
    }

    private async Task<List<ReadingPoint>> FetchReadingsWindowAsync(
        string usagePointId,
        string database,
        int scaler,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        var payload = Payload(
            ("method", "readings"),
            ("usage-point-id", usagePointId),
            ("database", database),
            ("fromtime", fromUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")),
            ("totime", toUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));

        var json = await PostJsonAsync(payload);
        var rows = ParseReadingsRows(json, scaler).ToList();
        rows.Sort(static (a, b) => a.TargetTimeUtc.CompareTo(b.TargetTimeUtc));
        return rows;
    }

    private async Task<byte[]> PostJsonAsync(Dictionary<string, object?> payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = Encoding.UTF8.GetByteCount(json);

        using var req = new HttpRequestMessage(HttpMethod.Post, location) { Content = content };
        req.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (res.IsSuccessStatusCode)
            {
                return await res.Content.ReadAsByteArrayAsync();
            }

            var bodyText = await res.Content.ReadAsStringAsync();
            bodyText = bodyText.Replace("\r", " ").Replace("\n", " ").Trim();
            throw new InvalidOperationException($"SMGW returned {(int)res.StatusCode} {res.ReasonPhrase}. Body: {bodyText}");
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new InvalidOperationException("TLS/auth handshake failed.", ex);
        }
    }

    private static Dictionary<string, object?> Payload(params (string Key, object? Value)[] fields)
    {
        var d = new Dictionary<string, object?>(fields.Length, StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            d[key] = value;
        }
        return d;
    }

    private static IEnumerable<ReadingPoint> ParseReadingsRows(byte[] readingsJson, int scaler)
    {
        using var doc = JsonDocument.Parse(readingsJson);
        if (!doc.RootElement.TryGetProperty("readings", out var readings)) yield break;
        if (!readings.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array) yield break;

        // Simplification: treat the first channel as the series we want.
        JsonElement? firstChannelWithReadings = null;
        foreach (var channel in channels.EnumerateArray())
        {
            if (channel.TryGetProperty("readings", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                firstChannelWithReadings = channel;
                break;
            }
        }
        if (firstChannelWithReadings is null) yield break;

        var readingsArr = firstChannelWithReadings.Value.GetProperty("readings");
        foreach (var v in readingsArr.EnumerateArray())
        {
            var targetTime = v.GetPropertyOrNull("target-time");
            var rawValue = v.GetPropertyOrNull("value");
            if (string.IsNullOrWhiteSpace(targetTime) || string.IsNullOrWhiteSpace(rawValue)) continue;
            if (!DateTimeOffset.TryParse(targetTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) continue;
            dto = dto.ToUniversalTime();

            yield return new ReadingPoint(dto, Scale(rawValue!, scaler));
        }
    }

    private static decimal? Scale(string rawValue, int scaler)
    {
        if (!decimal.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            return null;
        }

        // DIN 62056-62 style scaler: value * 10^scaler
        if (scaler == 0) return v;
        var abs = Math.Abs(scaler);
        decimal pow = 1m;
        for (var i = 0; i < abs; i++) pow *= 10m;
        return scaler > 0 ? v * pow : v / pow;
    }
}

public sealed record UsagePoint(string UsagePointName, string TafNumber, string UsagePointId);

public sealed record OriginSeries(UsagePoint UsagePoint, int Scaler);

public sealed record ReadingPoint(DateTimeOffset TargetTimeUtc, decimal? Value);

internal static class JsonElementExtensions
{
    public static string? GetPropertyOrNull(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var prop)) return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString()?.Trim(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => prop.ToString(),
        };
    }
}
