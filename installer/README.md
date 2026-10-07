# Windows installer build

The release workflow publishes self-contained `git-kanka.exe` and `KankaGitSync.Setup.exe` files for `win-x64` and `win-arm64`, then compiles this Inno Setup definition.

The installer is per-user, adds its application directory to the user PATH, and launches the setup wizard. It requires Git for Windows but does not bundle Git or Git Bash. Current installer builds are unsigned tester artifacts: Windows SmartScreen can warn or block them. Testers must download only from this repository's Actions artifacts and verify the adjacent SHA-256 checksum before running them.
