# Disposable-campaign acceptance

Run these checks with a disposable campaign and a separate world repository before any production use. Keep the token in an environment variable. Arrange an editing pause while applying writes because the API's final read/write race is not eliminated.

1. Create representative entities: character, location, organisation, tag, a private entity, and a player-editable entity. Include properties, posts with different visibility, relations, cross-references, raw HTML, and unsupported metadata.
2. Initialize and commit configuration; import. Confirm `git diff main..kanka/live` is empty. Run validation and plan; require zero mutations. Fetch again and confirm no extra commit.
3. Edit a post as a player. Fetch and inspect content and ambiguous polling provenance. Attempt push; require a block and no remote writes. Review through pull and commit.
4. Edit another entry manually as the owner. Require the same protection. Check configured owner/player status labels without asserting exact polling authorship.
5. Make a local prose edit and commit it. Plan must contain only that resource's managed fields. Push, fetch, and plan again; require zero writes. Verify unmanaged metadata and permissions remain intact.
6. Repeat with tags, a property, a post, and a relation. Inspect private visibility and post permissions directly in Kanka after each update.
7. Create two published local entities referencing each other. Inspect the plan, acknowledge publication, and push. Confirm stable local/numeric mappings, working references, and zero writes on a second push.
8. Remove a local file. Confirm no deletion operation. Add an explicit tombstone; confirm it is visible in the plan and execution is blocked in v0.1.
9. Make non-overlapping local/remote structured edits and pull; inspect the staged merge. Make competing prose edits and confirm an unresolved Git conflict. Change privacy on both sides and require review even if both changes agree.
10. Interrupt a push after a successful mutation. Confirm refetch or an actionable recovery failure, preserved successful operation IDs, and no blind replay. Test a create with a lost response, reconcile the actual existing object manually, and only then acknowledge recovery.
11. Inspect Git files and logs for token leakage without printing the token. Confirm every new local file outside world/ remains unpublished.

Record the Kanka campaign capabilities, authenticated role, observed API shapes, versions, and results. Failure of any preservation or zero-change check blocks production adoption. The server concurrency limitation is not solved by passing these checks.
