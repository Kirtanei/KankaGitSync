using KankaGitSync.Domain;

namespace KankaGitSync.Presentation;

internal sealed record CommandRequest(string[] Arguments)
{
    public static CommandRequest Parse(string[] arguments)
    {
        var valid = arguments[0] switch
        {
            "init" => arguments.Length == 3 && arguments[1] == "--campaign",
            "publish" or "delete" => arguments.Length == 2 && Canonical.ValidId(arguments[1]),
            "push" => arguments.Skip(1).Distinct(StringComparer.Ordinal).Count() == arguments.Length - 1 &&
                arguments.Skip(1).All(argument => argument is "--approve-privacy" or "--allow-delete"),
            "doctor" => arguments.Length == 1 || arguments.Length == 2 && arguments[1] == "--acknowledge-recovery",
            "fetch" => arguments.Length == 1 || arguments.Length == 2 && arguments[1] == "--full",
            "init-env" or "import" or "status" or "diff" or "pull" or "validate" or "plan" or "update" => arguments.Length == 1,
            _ => false
        };
        if (!valid) throw new SyncException("Unknown command or arguments. Run git kanka help.");
        return new CommandRequest(arguments);
    }
}
