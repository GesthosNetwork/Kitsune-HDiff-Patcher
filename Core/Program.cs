using System.Reflection;

namespace Patcher;

internal static class Program
{
    internal static string AppVersion { get; private set; } = "unknown";

    static int Main(string[] args)
    {
        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "unknown";

        int result = Patch.Run(args);

        if (result == 0)
        {
            bool verifyOk = Verify.Run();

            if (!verifyOk)
            {
                result = 1;
            }
        }
        else
        {
            Logger.Error("Patching failed.");
        }

        Console.Write("\nPress any key to exit...");
        Console.ReadKey(true);

        return result;
    }

    internal static void SetTitle(string status)
    {
        Console.Title = $"Kitsune HDiff Patcher v{AppVersion} - {status}";
    }
}
