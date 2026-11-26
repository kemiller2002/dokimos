# DOK-ARCH-005 — Cache dependency ban

Status: implemented (`dokimos architecture check`; requirement RQ-DOKIMOS-ARCH-001, portfolio requirement EAR-QRY-008).

## Activation

Active only when the analyzed repository's `.echelon/reliability.json` (`echelon.reliability/v1`, Praxis
RQ-ROS-2026-A038) declares `cacheReadiness` `applicable` or `partially-applicable`. A declaration of
`not-applicable`, or no declaration at all, reports the rule as `not-applicable`, which is never a pass. A
declaration that cannot be decoded makes the rule `unavailable` (exit 3).

## Rule

No MSBuild project file (`*.fsproj`, `*.csproj`, `*.vbproj`, `Directory.Build.props`, `Directory.Build.targets`,
`Directory.Packages.props`) may reference a cache-provider or distributed-cache package through
`<PackageReference Include=...>` or `<GlobalPackageReference Include=...>`, unless a valid exception accepts that
exact dependency.

Banned (`ReliabilityArchitecture.cacheProviders`; "family" means the id and every id starting with `<id>.`;
comparison ignores case):

| Package | Match |
|---|---|
| `StackExchange.Redis` | family |
| `Microsoft.Extensions.Caching.StackExchangeRedis`, `Microsoft.Extensions.Caching.Redis` | exact |
| `Microsoft.Extensions.Caching.Memory`, `.SqlServer`, `.Cosmos`, `.Hybrid` | exact |
| `Microsoft.AspNetCore.OutputCaching.StackExchangeRedis` | exact |
| `Alachisoft.NCache`, `NCache` | family |
| `EasyCaching`, `ZiggyCreatures.FusionCache`, `LazyCache`, `CacheManager` | family |
| `EnyimMemcachedCore`, `Enyim.Caching` | family |

Deliberately **not** banned: `Microsoft.Extensions.Caching.Abstractions`. It contains only the `IMemoryCache` and
`IDistributedCache` interfaces. Depending on the abstraction without a provider is what "cache-ready, not
cache-dependent" asks for.

## Finding and scope

One finding per banned reference: scope `<repository-relative project file>#<PackageId>`, with file and line.
Verdict `violation`, exit 4.

## Accepting a dependency

Add an entry to `quality/exceptions.json` with `ruleId: "DOK-ARCH-005"`, the exact scope, rationale, owner,
created, `expires` or `reviewCondition`, and `evidence` that names a decision record id (`DF-...` or `ADR-...`).
An entry without a decision record id, or an expired one, is invalid (verdict `invalid-exceptions`, exit 6) and
accepts nothing.

## What a pass does not establish

- Runtime behaviour: a pass is static evidence only. It says nothing about whether the application tolerates a
  cold, stale or missing cache.
- Transitive dependencies, framework references (`Microsoft.AspNetCore.App` ships
  `Microsoft.Extensions.Caching.Memory`, so `AddMemoryCache()` in an ASP.NET Core app is not detected),
  `paket.dependencies`, and MSBuild property-valued includes (`Include="$(Pkg)"`) are not analyzed.
- Ecosystems other than .NET: with no MSBuild project file the rule is `unavailable`, not a pass.
- An accepted exception proves that a decision record id is named, not that the decision was accepted.
