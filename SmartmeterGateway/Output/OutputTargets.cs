namespace SmartmeterGateway.Output;

internal sealed record OutputTargets
{
    public CsvTarget Csv { get; init; } = new(true);
    public InfluxDbTarget InfluxDb { get; init; } = new(false);
    public SqliteTarget Sqlite { get; init; } = new(false);
}

internal interface IOutputTarget
{
    bool Enabled { get; }
    ISeriesOutput CreateOutput();
}

internal sealed record CsvTarget(bool Enabled) : IOutputTarget
{
    public ISeriesOutput CreateOutput() => new CsvSeriesOutput(this);
}

internal sealed record InfluxDbTarget(
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

internal sealed record SqliteTarget(
    bool Enabled,
    string DatabasePath = "timeseries.sqlite") : IOutputTarget
{
    public ISeriesOutput CreateOutput() => new SqliteSeriesOutput(this);
}