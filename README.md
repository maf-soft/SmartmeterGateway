# SmartmeterGateway

Minimal .NET 8 console tool to download available 15-minute smart meter readings (consumption + feed-in) from an SMGW (IF_GW_CON / m2m) and export them to CSV, InfluxDB, and/or SQLite.

![Grafana screenshot](doc/Grafana.png)

## Requirements

- .NET SDK 8.x
- Network access to your gateway

## API documentation

- Compact IF_GW_CON notes: [`doc/IF_GW_CON_API.md`](doc/IF_GW_CON_API.md)

## Configuration

Main config file:

- `SmartmeterGateway/appsettings.json`

Template:

- [`SmartmeterGateway/appsettings.example.json`](SmartmeterGateway/appsettings.example.json)

Important fields:

- `OutputRoot`: target directory (default: `output`)
- `Polling`
  - `IntervalMinutes`: distance between polling ticks in minutes; ticks are always aligned to interval slots (plus `OffsetMinutes`), not relative to process start
  - `OffsetMinutes`: delay after aligned slot boundary before polling (for example `15+1` -> `xx:01/16/31/46`)
- `Outputs`
  - `Csv.Enabled`: enable/disable CSV output
  - `InfluxDb.Enabled`: enable/disable InfluxDB output
  - `InfluxDb.Url` / `Org` / `Bucket` / `Token`
  - `InfluxDb.Measurement` (default: `smartmeter_readings`)
  - `InfluxDb.AllowInvalidServerCertificate`
  - `Sqlite.Enabled`: enable/disable SQLite output
  - `Sqlite.DatabasePath` (default: `timeseries.sqlite`, relative to `OutputRoot`)
- `Meters[]`: list of gateway endpoints
  - `Active`: `true`/`false`
  - `Name`: optional (used for output folder naming)
  - `BaseUrl`: e.g. `https://192.168.178.3/`
  - `Username` / `Password`
  - `AllowInvalidServerCertificate`: disables TLS certificate validation (local/debug only)

Do not commit credentials from `appsettings.json`.

## Build and run

```bash
dotnet build .\SmartmeterGateway\SmartmeterGateway.csproj -c Release
dotnet run --project .\SmartmeterGateway\SmartmeterGateway.csproj -c Release
```

Optional continuous polling mode:

```bash
dotnet run --project .\SmartmeterGateway\SmartmeterGateway.csproj -c Release -- --poll
```

Manual CSV -> SQLite import (explicit, no auto-discovery):

```bash
dotnet run --project .\SmartmeterGateway\SmartmeterGateway.csproj -c Release -- --sqlite-import-csv --meter-key <meter-key> --series <BEZUG|EINSP> --csv <path-to-csv>
```

Behavior:

- Startup always performs the normal catch-up run first.
- Without `--poll`, the process exits after catch-up.
- With `--poll`, the process keeps running and fetches only data newer than the last successful in-memory cursor.
- Stop the process gracefully with `Ctrl+C`.

## Releases

- Prebuilt executables are published in GitHub Releases:
  - https://github.com/maf-soft/SmartmeterGateway/releases/latest
- Download the archive for your platform (for example `win-x64`, `linux-x64`, `linux-arm64`).

## Output files

For each active meter, files are written to `OutputRoot/<meterKey>/`:

- `user-info.json`
- `usage-point-info-*.json`
- `readings-15min-bezug.csv`
- `readings-15min-einsp.csv`

If SQLite output is enabled, one SQLite database file is also written to `OutputRoot/<DatabasePath>`.

CSV format:

- Header: `TargetTimeUtc,Value`
- UTC timestamps

## Incremental behavior

Each enabled output keeps its own cursor:

- CSV: reads the last timestamp/value from the CSV file.
- InfluxDB: queries the latest point (`time` and `value`) from the configured measurement, filtered to this source (`meter`, `direction`, `database='origin'`).
- SQLite: queries the latest row from `raw_readings` filtered to this source (`source_type='smartmeter'`, `source_id`, `series`).

If any enabled output has no cursor, a backfill is triggered so all outputs can catch up.

## Grafana (SQLite recommended)

Importable dashboards:

- SQLite: [`doc/grafana-dashboard-sqlite.json`](doc/grafana-dashboard-sqlite.json)
- InfluxDB (optional): [`doc/grafana-dashboard-influx.json`](doc/grafana-dashboard-influx.json)

### SQLite datasource plugin setup

1. Install plugin `frser-sqlite-datasource` in Grafana.
2. Restart Grafana.
3. Create a new datasource of type `SQLite`.
4. Set the database path to your generated SQLite file (usually `output/timeseries.sqlite` relative to this repo).
5. Verify datasource with `Save & Test`.

If Grafana runs in Docker/another host, the SQLite file must be reachable from that runtime (mount/bind path accordingly).

### SQLite dashboard usage notes

- Daily/Monthly panels are grouped by local calendar boundaries via SQLite `localtime` conversion.
- Variables `meter_netz` and `meter_haus` are runtime selections; no fixed meter IDs are stored in dashboard JSON.
- The dashboard default time range is `now-7d`.

## InfluxDB (optional)

Influx setup, limitations, and query details are documented here:

- [`doc/INFLUX_DETAILS.md`](doc/INFLUX_DETAILS.md)

## Grafana example queries (SQLite)

These queries are designed for a two-meter cascade (`Netz` and `Haus`), for example with a PV system between both meters.

Role model used in the dashboard:

- `meter_netz`: grid-side meter (`Netz`)
- `meter_haus`: house-side meter (`Haus`)

Signed model:

- `BEZUG` positive
- `EINSP` negative
- `Produktion = Haus - Netz`

Recommended Grafana template variables:

- `meter_netz`: physical meter ID used as grid meter (`Netz`)
- `meter_haus`: physical meter ID used as house meter (`Haus`)

Conventions used by the SQLite queries:

- Input values are cumulative Wh counters in `raw_readings` (`series = BEZUG/EINSP`).
- Deltas are computed with `LAG(value)` per `(source_id, series)`.
- In the power panel, `delta * 4.0` converts Wh per 15-minute slot to average W:
  - 1 hour = 4 * 15 minutes
  - therefore `Wh / 0.25h = Wh * 4 = W`
- Signed logic: `BEZUG` is positive, `EINSP` is negative.

Grafana panel hints:

- Use `time` as time column and format as time series.
- The wide query shape (`MAX(CASE WHEN ...)`) yields one series per named column.
- For the top power panel, **Fill opacity** around `25` keeps the area chart readable.

### 1) Smartmeter - 15-min average power (SQLite)

```sql
WITH d AS (
  SELECT
    target_time_utc AS time,
    source_id AS meter,
    series,
    (value - LAG(value) OVER (PARTITION BY source_id, series ORDER BY target_time_utc)) * 4.0 AS delta
  FROM raw_readings
  WHERE source_type = 'smartmeter'
    AND series IN ('BEZUG','EINSP')
    AND source_id IN ('${meter_netz}','${meter_haus}')
    AND unixepoch(target_time_utc) >= $__from / 1000
    AND unixepoch(target_time_utc) < $__to / 1000
),
signed AS (
  SELECT
    time,
    meter,
    SUM(CASE WHEN series='BEZUG' THEN delta ELSE -delta END) AS value
  FROM d
  GROUP BY time, meter
)
SELECT
  time,
  MAX(CASE WHEN meter='${meter_netz}' THEN value END) AS "Netz",
  MAX(CASE WHEN meter='${meter_haus}' THEN value END) AS "Haus",
  MAX(CASE WHEN meter='${meter_haus}' THEN value END) - MAX(CASE WHEN meter='${meter_netz}' THEN value END) AS "Produktion"
FROM signed
GROUP BY time
ORDER BY time;
```

### 2) Daily/Monthly sums (SQLite)

```sql
WITH d AS (
  SELECT
    target_time_utc AS time,
    source_id AS meter,
    series,
    (value - LAG(value) OVER (PARTITION BY source_id, series ORDER BY target_time_utc)) AS delta_wh
  FROM raw_readings
  WHERE source_type = 'smartmeter'
    AND series IN ('BEZUG','EINSP')
    AND source_id IN ('${meter_netz}','${meter_haus}')
    -- Monthly variant: '-2 years'
    AND unixepoch(target_time_utc) >= unixepoch('now', '-90 days')
),
agg AS (
  SELECT
    -- Daily variant:
    unixepoch(date(time, 'localtime') || ' 00:00:00', 'utc') AS time,
    -- Monthly variant:
    -- unixepoch(strftime('%Y-%m-01', time, 'localtime') || ' 00:00:00', 'utc') AS time,
    meter,
    series,
    SUM(delta_wh) / 1000.0 AS kwh
  FROM d
  WHERE delta_wh IS NOT NULL
  GROUP BY 1, 2, 3
),
net AS (
  SELECT
    time,
    meter,
    MAX(CASE WHEN series='BEZUG' THEN kwh END) AS bezug_kwh,
    MAX(CASE WHEN series='EINSP' THEN kwh END) AS einsp_kwh
  FROM agg
  GROUP BY time, meter
)
SELECT
  time,
  MAX(CASE WHEN meter='${meter_netz}' THEN bezug_kwh END) AS "Netz Bezug",
  MAX(CASE WHEN meter='${meter_haus}' THEN bezug_kwh END) AS "Haus Bezug",
  -MAX(CASE WHEN meter='${meter_netz}' THEN einsp_kwh END) AS "Netz Einspeisung",
  -MAX(CASE WHEN meter='${meter_haus}' THEN einsp_kwh END) AS "Haus Einspeisung",
  MAX(CASE WHEN meter='${meter_haus}' THEN bezug_kwh - einsp_kwh END)
  - MAX(CASE WHEN meter='${meter_netz}' THEN bezug_kwh - einsp_kwh END) AS "Produktion"
FROM net
GROUP BY time
ORDER BY time;
```

SQLite timezone note:

- Daily/Monthly grouping is intentionally based on local calendar boundaries (`localtime`) before converting back to UTC epoch for Grafana.
- This keeps midnight assignment aligned with local day/month semantics in the SQLite dashboard.
- `localtime` uses the timezone of the system where SQLite runs (Grafana host), not the browser timezone.

## Notes

- Some gateways are strict with JSON/HTTP framing. This tool sends `Content-Type: application/json` without charset and sets `Content-Length` explicitly.
- Gateway reading windows are limited to ~31 days, so data is fetched in chunks.
- `AllowInvalidServerCertificate=true` should only be used for local testing.

## Platform support

- Development and testing so far was done on **Windows**.
- The project should also run on **Linux** because it is a .NET 8 console app and uses no Windows-only APIs in the code path.
- For Linux usage:
  - install .NET SDK 8.x
  - use Linux paths in your shell commands (for example `dotnet run --project ./SmartmeterGateway/SmartmeterGateway.csproj -c Release`)
  - provide reachable gateway/IP routing and valid credentials in `appsettings.json`

## Gateway networking (fixed IP setups)

- Some gateways use fixed/default IP setups that do not integrate easily into a normal home LAN.
- Practical options can include:
  - direct cable to a dedicated NIC/port on the PC
  - a router/NAT setup that maps IP/port between gateway segment and local LAN
- A MikroTik router has been used successfully for this integration in practice.
- If you run into this networking topic, questions are welcome.

## Author

- Moritz Franckenstein
- GitHub: `maf-soft`
- Email: `^ [at] gmx.net`

## License

MIT License. See [LICENSE](LICENSE).
