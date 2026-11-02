# Benchmark 2: evidence collection (provider boundary)

A library and store that fetch evidence from an external HTTP provider, attribute it, and save and reload it with an integrity check. These requirements and `CONTRACT.md` are the only inputs. The implementation and the executable tests are produced during the benchmark, by separate agents.

## EVD-001 Attribution

- **EVD-001-O1:** `Evidence.create` refuses a record whose `Source` or `CollectedBy` is empty or whitespace with `MissingAttribution field`, naming the missing field.
- **EVD-001-O2:** a record with both attribution fields is created, and its `Sha256` is the lowercase hex SHA-256 of its `Content`.

## EVD-002 Provider contract

The provider is reached over real HTTP: `GET {baseUrl}/evidence/{id}` with header `Authorization: Bearer {token}`.

- **EVD-002-O1:** a 200 answer returns `Ok` with exactly the response body bytes.
- **EVD-002-O2:** 404 maps to `Error(ProviderNotFound id)`, 401 or 403 to `Error ProviderUnauthorized`, and 429 to `Error ProviderRateLimited`. None of them ever returns `Ok`.
- **EVD-002-O3:** every request carries the bearer token, and a request to the provider never carries it anywhere else (not in the URL or query).

## EVD-003 Integrity on reload

- **EVD-003-O1:** a saved record loads back equal to the saved record.
- **EVD-003-O2:** if the stored content is altered after saving, `load` returns `Error(IntegrityFailure id)`, never `Ok`.

## EVD-004 Large read-back

- **EVD-004-O1:** a record whose content is larger than 2 MiB, fetched from the provider over HTTP and then saved and loaded, loads back with every byte equal.

## EVD-005 Credential confinement

- **EVD-005-O1:** the token's value never appears in any `ProviderError`, any exception message, or any line written to the collector's diagnostic log (`Collector.Diagnostics`), including for 401 and network failures.
