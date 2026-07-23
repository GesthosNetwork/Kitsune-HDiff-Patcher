namespace Patcher;

internal static class Logger
{
    private static string Time()
    {
        return DateTime.Now.ToString("HH:mm:ss");
    }

    public static void Info(string message)
    {
        Write("INFO", message, ConsoleColor.Green);
    }

    public static void Success(string message)
    {
        Write("SUCC", message, ConsoleColor.Blue);
    }

    public static void Warning(string message)
    {
        Write("WARN", message, ConsoleColor.Yellow);
    }

    public static void Error(string message)
    {
        Write("ERROR", message, ConsoleColor.Red);
    }

    private static void Write(string level, string message, ConsoleColor color)
    {
        string prefix = $"[{Time()}]";
        string tag = $"[{level}]";

        string[] lines = message.Replace("\r\n", "\n").Split('\n');

        Console.Write(prefix);
        Console.ForegroundColor = color;
        Console.Write(tag);
        Console.ResetColor();
        Console.Write($" {lines[0]}");

        string indent = new string(' ', prefix.Length + tag.Length + 1);

        for (int i = 1; i < lines.Length; i++)
        {
            Console.WriteLine();
            Console.Write(indent);
            Console.Write(lines[i]);
        }

        Console.WriteLine();
    }
}
