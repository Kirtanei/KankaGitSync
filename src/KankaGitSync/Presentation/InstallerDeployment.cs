namespace KankaGitSync.Presentation;

internal static class InstallerDeployment
{
    private const string MarkerName = "kanka-installer.marker";

    public static bool IsInstalled() => File.Exists(Path.Combine(AppContext.BaseDirectory, MarkerName));
}
