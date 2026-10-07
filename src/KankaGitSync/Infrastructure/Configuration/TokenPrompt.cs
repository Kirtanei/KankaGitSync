using System.Text;

namespace KankaGitSync.Infrastructure.Configuration;

public static class TokenPrompt
{
    public static string? Read()
    {
        if (Console.IsInputRedirected) return Console.ReadLine();
        var previous = Console.TreatControlCAsInput;
        try
        {
            Console.TreatControlCAsInput = true;
            return ReadHidden();
        }
        finally { Console.TreatControlCAsInput = previous; }
    }

    private static string ReadHidden()
    {
        var token = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return token.ToString();
            if (key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (token.Length > 0) token.Length--;
            }
            else if (!char.IsControl(key.KeyChar)) token.Append(key.KeyChar);
        }
    }
}
