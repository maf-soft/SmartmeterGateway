namespace SmartmeterGateway.Output;

internal sealed record OutputTargets(CsvTarget Csv, InfluxDbTarget InfluxDb);

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