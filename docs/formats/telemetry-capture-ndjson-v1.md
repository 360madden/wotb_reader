# Telemetry capture NDJSON v1

Status: alpha contract  
Last updated: 2026-07-26

Telemetry capture is deliberately separate from application logging. It is a
newline-delimited JSON evidence format intended for later comparison with
decoded replay telemetry. Application logs must never contain these payloads.

Each UTF-8 line is one JSON object:

```json
{
  "schemaVersion": "1",
  "sourceSequence": 42,
  "sourceTimeUtc": "2026-07-26T20:00:00Z",
  "replayTimeMs": 1250.0,
  "eventType": "position",
  "participantIdentity": "explicit-source-identity",
  "entityId": 17,
  "values": {
    "x": 12.5,
    "y": 4.0,
    "z": -8.5
  },
  "provenance": {
    "sourceVersion": "capture-1",
    "detail": "optional bounded source description"
  }
}
```

`sourceSequence` and `eventType` are required. Source and replay time may be
null, but an event without a time basis cannot be timestamp-compared.
`participantIdentity` is never guessed; `entityId` is preferred when both
sources provide it. `values` must be an object. Unknown fields are tolerated
only at the JSON envelope level and do not become canonical semantics.

The reader enforces byte-per-line, event-count, duration, JSON-depth, numeric,
and cancellation limits. A malformed line produces a typed result and never
terminates the host.

## Comparison rules

The v1 comparator:

1. requires exact entity IDs when either side provides one;
2. otherwise requires an exact, explicitly supplied participant identity;
3. requires the same event type;
4. chooses the nearest unmatched timestamp inside the configured window
   (250 ms by default);
5. compares JSON values exactly unless that field has an explicit numeric
   tolerance.

Results keep `exact`, `tolerant`, `mismatch`, `missing`, `extra`, and
`uncomparable` counts separate. A timestamp-window match is `tolerant` when
values otherwise match. No fuzzy-name or inferred bot matching is permitted.
