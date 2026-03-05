namespace SmartmeterGateway.Output;

internal sealed record OutputCursor(DateTimeOffset TimestampUtc, decimal? Value);

internal sealed record OutputWriteResult(int WrittenCount, string TargetDescription);

internal interface ISeriesOutput : IDisposable
{
    string Name { get; }

    Task<OutputCursor?> TryGetCursorAsync(string meterKey, string outDir, OriginSeries series);

    Task<OutputWriteResult> WriteAsync(string meterKey, string outDir, OriginSeries series, IReadOnlyList<ReadingPoint> points, bool append);
}
