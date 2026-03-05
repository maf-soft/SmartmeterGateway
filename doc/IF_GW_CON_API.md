# IF_GW_CON (CONEXA 3.0 SMGW) – Compact Developer Notes

This document is a condensed, implementation-focused summary of the IF_GW_CON API.
It combines the official interface specification with practical behavior observed on a real gateway.

Scope:
- M2M calls for TAF1/TAF7
- Load-profile exports (15-minute values)
- Robust data retrieval patterns for production-like scripting

## Sources and assumptions

- Specification source: [`Schnittstellenbeschreibung IF_GW_CON.pdf`](Schnittstellenbeschreibung%20IF_GW_CON.pdf)
- Runtime behavior notes are marked as **Practice note** and come from live request/response traces.
- Gateway behavior may differ across firmware versions and deployments.

## Quick implementation flow

1. Discover POC endpoint:
   - `GET /smgw/m2m`
   - Expect `307 Temporary Redirect` and read `Location: /smgw/m2m/<userid>/json`
2. Discover available usage points:
   - `POST /smgw/m2m/<userid>/json` with `{ "method": "user-info" }`
3. Inspect channels and scaler/unit for a usage point:
   - `{ "method": "usage-point-info", "usage-point-id": "..." }`
4. Read values:
   - `{ "method": "readings", ... }`
   - Max query window: **31 days**
   - Optional quick check: `last-reading: true`

Current implementation note:
- The current exporter does not send an explicit OBIS filter in `readings`; it parses the first returned channel that contains readings.

Additional methods:
- `smgw-info`
- `log`
- `self-test`

## HTTP rules and interoperability

### Endpoint shape

- Entry point: `GET /smgw/m2m`
- API calls: `POST /smgw/m2m/<userid>/json` with JSON payload
- Use `Content-Type: application/json`

### Content-Type and body framing (**Practice note**)

This gateway is strict about JSON requests:

- Prefer exactly `Content-Type: application/json` (without `charset=utf-8`).
- If you get `400 Bad Request` with JSON parse errors:
  - avoid `application/json; charset=utf-8`
  - avoid chunked transfer when possible (set explicit `Content-Length`)

This is why generic helpers like `PostAsJsonAsync` may fail on embedded stacks.

### Authentication (**Practice note**)

- Usually mutual TLS or HTTP Digest.
- With Digest, the first request may return `401`, followed by a successful authenticated retry.

### Status codes (practical subset)

- `307 Temporary Redirect`: resolve concrete `<userid>` path via `Location`
- `200 OK`: success
- `400 Bad Request`: malformed JSON/parameters
- `401 Unauthorized`: missing or invalid auth
- `404 Not Found`: wrong path
- `405 Method Not Allowed`: wrong HTTP method
- `500 Internal Server Error`: gateway/server failure

## Time formats

Supported format family:
- Date: `YYYY-MM-DD`
- Time: `hh:mm:ss`
- DateTime: `YYYY-MM-DDThh:mm:ss`

Timezone forms:
- UTC: `Z`
- Offset: `+hh:mm` / `-hh:mm`

**Practice note (timezone boundaries)**:
Human-local boundaries (month/year 00:00 CET) may appear as `23:00Z` in UTC.

## Method reference (compact)

### A-2.1 Root (POC endpoint discovery)

Request:

```http
GET /smgw/m2m
```

Behavior:
- On successful auth: `307` + `Location: /smgw/m2m/<userid>/json`

Implementation tip:
- Do not auto-follow redirects blindly; extract and persist `Location`.

### A-2.2 `smgw-info`

Request:

```json
{ "method": "smgw-info" }
```

Useful fields:
- `smgw-id`, `smgw-time`, `smgw-state`
- `firmware-info.{version,hash,component}`
- certificate metadata

### A-2.3 `user-info`

Request:

```json
{ "method": "user-info" }
```

Useful fields from `user-info.usage-points[]`:
- `usage-point-id` (key for further calls)
- `taf-number` (e.g., 1 or 7)
- `taf-state`
- `meter[].meter-id`
- `metering-point-id`
- `billingPeriods[]`
- `start-time`, `end-time`

**Practice note**:
`billingPeriods.start-time` is not guaranteed to be the earliest available reading timestamp.

### A-2.4 `usage-point-info`

Request:

```json
{
  "method": "usage-point-info",
  "usage-point-id": "<usage-point-id>",
  "database": "<database>"
}
```

Parameters:
- `usage-point-id`: required
- `database`: optional (if omitted, all databases may be returned)

Database values (spec-level):
- `origin`, `derived`, `calculated`, `daily`

**Practice note**:
For full 15-minute history, `origin` was the only useful source in observed gateway behavior.

Current implementation note:
- Usage points are selected by name suffix (`BEZUG` / `EINSP`) and TAF priority (`TAF1` preferred, `TAF7` fallback).

Useful fields:
- `databases[].channels[]` with `obis`, `scaler`, `unit`

Scaling rule:

$$
value_{scaled} = value_{raw} \cdot 10^{scaler}
$$

### A-2.5 `log`

Request:

```json
{
  "method": "log",
  "fromtime": "<fromtime>",
  "totime": "<totime>",
  "fromindex": "<fromindex>",
  "count": 1500
}
```

Notes:
- All parameters optional
- `count` max observed/spec: `1500`

### A-2.6 `readings`

Request template:

```json
{
  "method": "readings",
  "usage-point-id": "<usage-point-id>",
  "database": "origin",
  "channels": [{ "channel": "<obis>" }],
  "last-reading": false,
  "fromtime": "<fromtime>",
  "totime": "<totime>"
}
```

Key behavior:
- Max range per request: **31 days**
- `last-reading: true` ignores `fromtime`/`totime`
- Omitting `channels` returns all channels

Response shape:
- `readings.channels[].readings[]`
- fields like `target-time`, `capture-time`, `value`, status fields, optional signature

**Practice notes**:
- For full export, use 31-day chunking.
- Deduplicate by `target-time` when merging chunks.
- This gateway behaves as if `fromtime` is effectively exclusive; start with a 15-minute safety overlap.

### A-2.7 `self-test`

Request:

```json
{ "method": "self-test" }
```

Behavior:
- Result is visible via `log`
- Cooldown: about 10 minutes between runs

## Fast smoke test (`last-reading`)

Minimal request:

```json
{
  "method": "readings",
  "usage-point-id": "<usage-point-id>",
  "database": "origin",
  "last-reading": true
}
```

Channel-filtered variant:

```json
{
  "method": "readings",
  "usage-point-id": "<usage-point-id>",
  "database": "origin",
  "channels": [{ "channel": "<obis>" }],
  "last-reading": true
}
```

**Practice note**:
On this gateway, channel-filtered `last-reading` can return empty arrays.
For pure "is live data available?" checks, the unfiltered variant is often more reliable.

Current implementation note:
- This project currently follows the unfiltered approach for `readings` and uses the first channel with values.

## Recommended implementation checklist

1. Resolve `Location` from root call (`307`)
2. Query `user-info` and select TAF1/TAF7 usage points
3. Query `usage-point-info` for `origin` and channel metadata
4. Read `readings` in <=31-day windows
5. Merge with overlap and deduplicate by `target-time`
6. Apply `scaler` before storing
7. Store timestamps in UTC consistently

## Multi-meter practice

- Process each meter/gateway independently
- Keep output paths separate per meter
- Be explicit about TLS behavior:
  - IP-based HTTPS endpoints often fail strict certificate validation
  - disable certificate validation only for controlled local/testing setups
