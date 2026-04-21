# InfluxDB Details (Optional)

This project supports InfluxDB output, but SQLite is the recommended durable primary store.

## Local quick setup (InfluxDB 3)

Example (PowerShell):

```powershell
Set-Location C:\InfluxDB3
.\influxdb3.exe serve --data-dir "C:\InfluxDB3\data" --node-id home
```

Set host/token once per shell session:

```powershell
$env:INFLUXDB3_HOST_URL = "http://127.0.0.1:8181"
$env:INFLUXDB3_AUTH_TOKEN = "<your-token>"
```

Create database:

```powershell
.\influxdb3.exe create database home
```

## Practical limitation (important)

In long-running tests with 15-minute raw smart meter counters, query planner limits were hit in InfluxDB 3 Core due to a large number of parquet files (for example: `scan operation on table ... exceeded file limit`).

What this means in practice:

- InfluxDB 3 Core can work for short-term/local setups.
- For long-running raw history without lifecycle management, it became unreliable in this project.
- Raising `--query-file-limit` can help temporarily, but increases resource pressure.

Recommendation:

- Use SQLite (or CSV) as primary durable target.
- Treat InfluxDB as optional secondary target for dashboards/experiments.

## Grafana query examples (InfluxDB)

Recommended Grafana template variables:

- `meter_netz`: physical meter ID used as grid meter (`Netz`)
- `meter_haus`: physical meter ID used as house meter (`Haus`)

Conventions used by the queries:

- Input values are cumulative Wh counters per `meter` and `direction` (`BEZUG`, `EINSP`).
- Delta is computed via `LAG(value)`.
- In the power query, `delta * 4` converts Wh per 15-minute slot to average W.
- Signed logic: `BEZUG` is positive, `EINSP` is negative.

### 1) Smartmeter - 15-min average power

```sql
WITH d AS (
  SELECT
    time,
    CASE WHEN meter='${meter_netz}' THEN 'Netz'
         WHEN meter='${meter_haus}' THEN 'Haus'
         ELSE meter END AS meter,
    direction,
    (value - LAG(value) OVER (PARTITION BY meter, direction ORDER BY time)) * 4 AS delta
  FROM home_readings
  WHERE "database"='origin'
    AND direction IN ('EINSP','BEZUG')
    AND $__timeFilter(time)
),
signed AS (
  SELECT
    time,
    meter,
    SUM(CASE WHEN direction='BEZUG' THEN delta ELSE -delta END) AS value
  FROM d
  GROUP BY time, meter
)
SELECT
  time,
  MAX(CASE WHEN meter='Netz' THEN value END) AS "Netz",
  MAX(CASE WHEN meter='Haus' THEN value END) AS "Haus",
  MAX(CASE WHEN meter='Haus' THEN value END) - MAX(CASE WHEN meter='Netz' THEN value END) AS "Produktion"
FROM signed
GROUP BY time
ORDER BY time;
```

### 2) Daily/Monthly sums

```sql
WITH d AS (
  SELECT
    time,
    CASE WHEN meter='${meter_netz}' THEN 'Netz'
         WHEN meter='${meter_haus}' THEN 'Haus'
         ELSE meter END AS meter,
    direction,
    (value - LAG(value) OVER (PARTITION BY meter, direction ORDER BY time)) AS delta_wh
  FROM home_readings
  WHERE "database"='origin'
    AND direction IN ('EINSP','BEZUG')
    -- Monthly variant: use INTERVAL '2 years'
    AND time >= NOW() - INTERVAL '90 days'
),
agg AS (
  SELECT
    -- Monthly variant: DATE_TRUNC('month', time)
    DATE_TRUNC('day', time) AS time,
    meter,
    direction,
    SUM(delta_wh) / 1000.0 AS kwh
  FROM d
  WHERE delta_wh IS NOT NULL
  GROUP BY 1, 2, 3
),
net AS (
  SELECT
    time,
    meter,
    MAX(CASE WHEN direction='BEZUG' THEN kwh END) AS bezug_kwh,
    MAX(CASE WHEN direction='EINSP' THEN kwh END) AS einsp_kwh
  FROM agg
  GROUP BY time, meter
)
SELECT
  time,
  MAX(CASE WHEN meter='Netz' THEN bezug_kwh END) AS "Netz Bezug",
  MAX(CASE WHEN meter='Haus' THEN bezug_kwh END) AS "Haus Bezug",
  -MAX(CASE WHEN meter='Netz' THEN einsp_kwh END) AS "Netz Einspeisung",
  -MAX(CASE WHEN meter='Haus' THEN einsp_kwh END) AS "Haus Einspeisung",
  MAX(CASE WHEN meter='Haus' THEN bezug_kwh - einsp_kwh END)
  - MAX(CASE WHEN meter='Netz' THEN bezug_kwh - einsp_kwh END) AS "Produktion"
FROM net
GROUP BY time
ORDER BY time;
```

## Timezone caveat (current InfluxDB 3 Core / FlightSQL)

Timezone-cast expressions (`AT TIME ZONE ...`) currently fail in this setup with Arrow cast errors.

As a result:

- Influx daily/monthly buckets are currently computed with plain `DATE_TRUNC(...)` boundaries.
- For strict local-midnight grouping, use the SQLite dashboard as reference/source of truth.
