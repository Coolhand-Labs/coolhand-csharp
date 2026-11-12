# coolhand-csharp

## Setup

.NET 10 SDK (the libraries target net8.0 and net10.0). No separate install step: `dotnet` restores
on the first build. Without a local SDK, use Docker:

```bash
docker run --rm -v "$PWD":/w -w /w mcr.microsoft.com/dotnet/sdk:10.0 dotnet test Coolhand.sln
```

## Verify before committing

```bash
dotnet test Coolhand.sln
```

This is the single gate, and what CI runs (`.github/workflows/ci.yml`, on .NET 8 and 10:
`-p:TestTargetFramework=net8.0` runs the tests on 8). Builds treat warnings as errors and generate
XML docs for the public API, so an undocumented public member fails the build. A green run means a
green CI run.

The tests never touch the network, except one loopback round trip through `HttpListener`
(`RealSocketTests`). `tests/.../Support/Fakes.cs` has `FakeHandler`, an in-process stand-in for both
the LLM provider and the Coolhand API, and the tests assert the JSON actually posted.

## Pull requests

If a PR addresses one or more GitHub issues, prefix the PR title with a `[closes #N]` tag per issue.

## Changelog and versioning

Do not add `CHANGELOG.md` entries or bump the version on feature/fix branches or in PRs. Both are
written once, at release time. The version lives only in `Directory.Build.props`; it drives the
collector string and the User-Agent.

## Code conventions

- C# 12, nullable reference types, file-scoped namespaces. Public API: `sealed record`s with `init`
  properties, `...Async` methods taking a `CancellationToken`, `null` for "absent".
- `ArgumentException` for bad input, thrown synchronously before any request (async methods validate
  first, then hand off to a private async core). `CoolhandException` / `CoolhandHttpException` for
  API failures. Submission methods (`LogRequestAsync`, `CreateFeedbackAsync`, uploads) never throw for
  API or network failures; they return `null`, as coolhand-node resolves to `null`.
- Capture must never break the host's request: everything `CoolhandHandler` does around the host
  call is guarded and degrades to a pass-through.
- `CoolhandLabs.Internal` is not API. The core package takes no dependencies beyond the base library;
  integrations live in their own packages.
- Source files: never put invisible or line-separator characters (U+2028, U+FEFF, ...) in literals;
  write `\uXXXX`.

**DRY:** check `BaseService` for an existing helper (request building, query strings, error shape,
pagination) before adding one. Capture logic lives once, in `Http/RequestMonitor.cs`; integrations
are thin.

## README and docs philosophy

The README is a landing page: install, quick start, what it supports, where to go next. Anything
needing more than one code block goes in `docs/`. Each read API has its own `docs/*.md`.

## Cross-SDK alignment

coolhand-node is the reference implementation for the Coolhand SDK family. Wire format (endpoints,
payload field names, collector strings), provider patterns (`api-patterns.json` is copied verbatim)
and redaction rules follow it. When porting a coolhand-node change, port its tests too, and record
any deliberate difference in the README's "Differences from coolhand-node" section.
