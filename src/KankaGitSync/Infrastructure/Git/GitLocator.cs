namespace KankaGitSync.Infrastructure.Git;

public static class GitLocator
{
    public static string Find()
    {
        var executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, executable));
        if (OperatingSystem.IsWindows())
            candidates = candidates.Concat([@"C:\Program Files\Git\cmd\git.exe", @"C:\Program Files (x86)\Git\cmd\git.exe"]);
        return candidates.FirstOrDefault(File.Exists) ?? throw new SyncException("Git was not found. Install Git for Windows, then try again.");
    }
}
