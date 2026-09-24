# Project guidance

Follow [the project ruleset](docs/project-ruleset-v0.1.md).

Use C# / .NET for the application and Vue if a frontend is introduced.
Use descriptive full-word names and keep cognitive complexity at or below 15.
Never write directly to Kanka outside the synchronization workflow.
Never store tokens in files or output them in diagnostics.
Preserve unknown remote fields; send only explicitly managed field patches.
Treat remote input, YAML, Git paths, and pagination URLs as untrusted.
Run release build, analyzer checks, and the full test suite after code changes.
Keep coverage output in the console; any temporary artifacts belong under ignored artifacts/.
Always commit and push completed changes after the required checks pass, unless the user explicitly instructs otherwise. Do not ask for separate confirmation; report any commit or push failure.
