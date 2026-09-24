# Security review

The implementation's security-relevant boundaries were reviewed alongside compiler and SonarAnalyzer diagnostics. A VS Code Problems integration and a connected SonarQube server were unavailable; this document is not a server-side quality-gate result.

| Boundary | Review and control |
| --- | --- |
| Bearer credentials | Environment input only; request Authorization header only; removed from Git subprocess environment. No HTTP response body or transport exception detail is printed. Responses containing the token, including JSON-escaped values or keys, are refused before persistence. |
| Pagination / SSRF | HTTPS, exact API host, port, and configured campaign path are enforced before attaching credentials. Redirects are disabled. Cycles and excessive pagination fail closed. |
| HTTP resources | Owned clients, requests, responses, and semaphores are disposed. Requests have timeouts; response buffering is bounded. Calls are awaited. Rate limiting and bounded 429 retries honor Retry-After. Failed non-429 writes are not automatically retried. |
| Filesystem paths | Local IDs have a restricted alphabet and length. Managed paths must remain under the selected repository and cannot traverse reparse points. Git-managed symlinks and submodules are refused. |
| Git execution | Absolute executable discovered from absolute PATH directories. No shell; arguments use ProcessStartInfo.ArgumentList. Text goes through stdin. Temporary index isolates fetch. Ref updates use expected old values. |
| YAML / JSON input | No arbitrary object deserialization. YAML aliases/anchors, ambiguous documents, duplicate keys, excessive depth, and oversized documents fail. Numeric metadata and visibility are validated before planning writes. |
| Publication and privacy | Allowlist rooted at world/. Opt-in publish flags. Explicit privacy/publication acknowledgement. Private entity shells. Minimal changed-field patches. No permissions or deletion API calls. |
| Concurrent edits | Fetch-before-push, ancestry block, per-resource reread, and post-write verification. The remaining API read/write race is explicitly unresolved; see architecture.md. |
| Recovery | Durable write-ahead intentions and returned IDs. Shared-repository lock. Recovery fetch uses its own cancellation budget. Original application/recovery failures retain their causes. Unknown outcomes block replay until human reconciliation. |
| HTML | Raw HTML is data, never executed by this application. Conversion falls back to original HTML if lossy. No browser frontend, template interpolation, or local HTML execution exists. |

Tests exercise hostile pagination URLs, secret-bearing/error responses, path escapes, malformed YAML, visibility gates, unknown write outcomes, and concurrent remote edits. Remaining high-impact limitations are the lack of server-side conditional writes, permission-dependent visibility, and unverified behavior against a real campaign.
