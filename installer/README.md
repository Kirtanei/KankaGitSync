# Windows installer build

The tag-triggered release workflow publishes self-contained `git-kanka.exe` and `KankaGitSync.Setup.exe` files for `win-x64` and `win-arm64`, then compiles this Inno Setup definition. Release assets are versioned as `KankaGitSync-<version>-win-<architecture>-Setup.exe`.

The installer is per-user, adds its application directory to the user PATH, and launches the setup wizard. It requires Git for Windows 2.44 or later but does not bundle Git or Git Bash. It marks its installation beside the executable so `git kanka update` reliably directs users to manual upgrades. Installers are unsigned: Windows SmartScreen or enterprise policy can block them. Download only from GitHub Releases, compare the SHA-256 with the release manifest, and run `gh attestation verify` before execution.
