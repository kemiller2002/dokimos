# Dokimos

Longitudinal code-quality evidence for Echelon repositories.

```bash
dotnet tool install --global EchelonFoundry.Dokimos.Cli --version <version>
dokimos version
dokimos snapshot src --repository owner/repo --revision "$(git rev-parse HEAD)" \
  --git-history git-history.txt --build-log build.log --test-results TestResults
dokimos evaluate --baseline baseline.json --current snapshot.json --policy .dokimos/policy.json
```

Exit codes: `0` continue, `1` unexpected fault, `2` invalid invocation or
configuration, `3` required evidence unavailable or invalid, `4` policy
failure, `5` evidence-store identity conflict.

Canonical JSON goes to stdout; diagnostics go to stderr. Contracts are
versioned (`dokimos version` lists them). Missing evidence is `unavailable`,
never zero. Source: https://github.com/kemiller2002/dokimos
