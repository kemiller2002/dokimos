# Current State

Updated: 2026-09-24

## Established

- Repository created and accessible.
- Project charter established.
- Comprehensive initial requirements established.
- Architecture boundary established.
- Requirement 0 makes longitudinal metrics mandatory.
- F# domain project created.
- Domain tests created for zero/unavailable semantics, incompatible metric definitions, and compatible deltas.
- GitHub Actions CI configured to restore, build, and test on main and pull requests.

## Not yet evidenced

The GitHub connector can write repository content but cannot execute the repository locally. Therefore no build or test is claimed as passing until CI evidence is observed.

## Next implementation sequence

1. Observe CI and repair compilation/test failures.
2. Add snapshot JSON schema and serialization boundary.
3. Add Git evidence adapter for file churn and modification history.
4. Add comparison engine and finding lifecycle.
5. Add baseline/ratchet policy.
6. Add first F#/.NET structural analyzer adapter.
7. Dogfood against Dokimos.
8. Persist first longitudinal baseline.
9. Add machine CLI.
10. Add Forma/Folio presentation surfaces.
