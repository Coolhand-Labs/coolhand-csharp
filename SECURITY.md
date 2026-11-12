# Security Policy

## Reporting a vulnerability

Please report security issues privately to security@coolhandlabs.com rather than opening a public
issue. We aim to acknowledge reports within two business days.

## What the SDK sends

Captured calls are redacted before they leave your process: credential headers, credential query
parameters and URL userinfo, and credential fields in JSON, NDJSON, form-encoded, multipart and
truncated bodies (see `docs/global-monitoring.md`). Your Coolhand API key is masked in error messages
and logs. `DryRun = true` (or `COOLHAND_DRY_RUN=true`) sends nothing at all.
