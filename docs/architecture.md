# Architecture and boundaries

`CommandLine` dispatches the Git-style CLI. `SyncService` orchestrates fetch, integration checks, planning, application, recovery, and verification. `GitRepository` uses Git plumbing with a temporary index, technical commit identity, and compare-and-swap ref updates. `WorldFiles`, `YamlCodec`, and `ContentCodec` define portable files. `KankaAdapter` owns numeric IDs and endpoint translations. `Planner` produces explicit operations. `PushExecutor` and `OperationLedger` handle non-atomic writes. `SemanticMerge` resolves independent structured fields while leaving hard conflicts reviewable.

There are no API DELETE calls and no writes to permissions endpoints. Missing local resources are ignored by the mutation planner. Missing remote resources disappear from `kanka/live` while ID mappings are retained to prevent accidental recreation.

## Branch and echo bookkeeping

Fetch creates a child of the previous `kanka/live` commit (or initial `main`) using an isolated Git index. It preserves non-managed repository files and does not check out the generated branch. No semantic/content change means no additional commit. Adapter snapshots intentionally retain original metadata for audit and detection of unknown changes.

Push requires `kanka/live` to be an ancestor of `main`. A successful verified push records two local refs: `refs/kanka/published` identifies its input `main` commit, and `refs/kanka/verified` identifies the exact observed output on `kanka/live`. That exact output is an accepted synchronization echo as long as `main` descends from the published input. Any subsequent remote change creates a different live commit and reinstates the integration block. These refs point into ordinary Git history; they do not implement another history database. They and the operation ledger are local to the clone. A fresh clone must fetch and reconcile instead of assuming an echo.

Creation records numeric IDs before subsequent operations. Circular references work because all entity shells are created first. Patches include changed managed fields only. Property patches also include the required name. Post permissions, layout, settings, and unmanaged fields are omitted. Every update is reread immediately before mutation, and its observed state must match the planned state. After application, verification checks expected managed values and unexpected changes in other observed resources.

Kanka's relation creation endpoint returns a collection that can include older relations to the same target. The HTTP adapter records existing relation IDs before creating, then enumerates the resulting collection and requires exactly one new matching resource. An ambiguous result becomes an unresolved ledger intention, never a guessed identity. This behavior was checked against the upstream [relation controller](https://github.com/owlchester/kanka/blob/develop/app/Http/Controllers/Api/v1/EntityRelationApiController.php).

## Support declaration

The ruleset reserves **FULL** for the entire CRUD lifecycle with proven round trips. No resource is claimed FULL in this non-destructive release: deletion and live-campaign round-trip certification remain deferred. The core adapters provide import/diff/create/update for entity common fields, tags, properties, posts, and relations under the explicit v0.1 mutation policy. Treat this as a documented interim extension to the three-level support model, not a claim of full protocol coverage.

| Resource surface | v0.1 behavior |
| --- | --- |
| Entity name, type, body, privacy, tags | Import, diff, create, update |
| Character title, age, sex, pronouns | Import, diff, create, update |
| Properties, posts, relations | Independent import, diff, create, update |
| Other type-specific fields and related metadata | READ_ONLY; preserved in API snapshots |
| Custom/unknown entity modules | UNMANAGED; generic record observed |
| Permissions and destructive operations | UNMANAGED; never mutated |
| Binary assets and external files | UNMANAGED; metadata only, no downloads |

Unknown property types, unknown visibility values, inaccessible relation targets, and unmappable tags stop import rather than silently discard data. Such a campaign needs adapter extension or visibility correction before synchronization. A full campaign export may therefore require more adapter coverage than the representative MVP fixtures.

## Known limits

- Kanka's documented API does not establish a conditional-write/compare-and-swap contract. The client guards each write with a reread, but an edit can still race between that read and the HTTP write. A post-write fetch cannot recover a value overwritten in this window. Coordinate an editing pause for publication. Strict race-free enforcement of the primary invariant requires an API concurrency primitive or a server-enforced editing lock; this implementation must not be described as providing that guarantee.
- API visibility is token-dependent. Use a campaign administrator with visibility of all managed content. Revoked visibility and deletion cannot always be distinguished from a list response alone.
- Fetch is a series of paginated reads, not an atomic server snapshot. Later fetches and pre-write guards reveal intervening changes. Polling authorship remains ambiguous.
- Preserved raw HTML is not rendered locally or sanitized by this CLI. Preview only in a renderer with appropriate HTML security controls. Imported unknown mention syntax may retain Kanka-specific references to prevent data loss.
- Nested metadata outside the supported schema is rejected for authored files. Detailed type-specific references and attachment options are preserved read-only until covered by a tested adapter.
- New entities stay private if a push fails before completion. Manual reconciliation is required for an API create whose response was lost; automated name matching would risk confusing unrelated resources.
- No production campaign or credentials were supplied during development. Offline fixture success is not the ruleset's full production-readiness certification.

## API references consulted

The adapter follows Kanka's [setup and rate limits](https://app.kanka.io/api-docs/1.0/setup), [entity identities](https://app.kanka.io/api-docs/1.0/entities), and [pagination](https://app.kanka.io/api-docs/1.0/misc/pagination). Related resources use the documented [property](https://app.kanka.io/api-docs/1.0/entities/attributes), [post](https://app.kanka.io/api-docs/1.0/entities/posts), and [relation](https://app.kanka.io/api-docs/1.0/entities/connections) endpoints. Bulk property replacement is deliberately avoided because it can delete omitted properties. No undocumented server dry-run or transaction behavior is assumed.
