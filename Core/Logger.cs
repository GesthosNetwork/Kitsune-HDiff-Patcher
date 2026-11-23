using System.Globalization;

namespace Patcher;

internal static class Logger
{
    private const int ProgressWidth = 36;
    private static readonly object ConsoleLock = new();

    private static bool progressActive;
    private static int progressLineLength;
    private static string? activeProgressLabel;
    private static int activeProgressCurrent;
    private static int activeProgressTotal;

    public static void Info(string message) => WriteLine("[INFO]", message, null);

    public static void Warning(string message) => WriteLine("[WARN]", message, ConsoleColor.Yellow);

    public static void Error(string message) => WriteLine("[ERROR]", message, ConsoleColor.Red);

    public static void Success(string message) => WriteLine("[SUCCESS]", message, ConsoleColor.Green);

    public static void Archive(string name, int index, int total)
    {
        Info($"Processing archive ({index}/{total}): {name}");
    }

    public static void Progress(string category, int current, int total)
    {
        if (total <= 0) { return; }

        current = Math.Clamp(current, 0, total);
        double percent = current / (double)total * 100d;
        WriteProgress(category, current, total, percent);
    }

    public static void ProgressPercent(string category, double percent, int current, int total)
    {
        if (total <= 0) { return; }

        current = Math.Clamp(current, 0, total);
        percent = Math.Clamp(percent, 0d, 100d);
        WriteProgress(category, current, total, percent);
    }

    private static void WriteProgress(string category, int current, int total, double percent)
    {
        int filled = percent >= 100d
            ? ProgressWidth
            : (int)Math.Floor(percent / 100d * ProgressWidth);
        string line = BuildProgressText(category, filled, percent, current, total);

        lock (ConsoleLock)
        {
            if (progressActive && !string.Equals(activeProgressLabel, category, StringComparison.Ordinal))
            {
                Console.WriteLine();
                ResetProgressState();
            }

            Console.Write('\r');
            Console.Write($"[{category}] [");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(new string('=', filled));
            if (filled < ProgressWidth)
            {
                Console.Write('>');
            }
            Console.ResetColor();

            if (filled < ProgressWidth)
            {
                Console.Write(new string('.', ProgressWidth - filled - 1));
            }

            string currentText = current.ToString("N0", CultureInfo.InvariantCulture);
            string totalText = total.ToString("N0", CultureInfo.InvariantCulture);
            string percentText = percent.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5);
            Console.Write($"] {percentText}% ({currentText}/{totalText})");

            ClearRemainder(line.Length);
            progressLineLength = line.Length;
            progressActive = true;
            activeProgressLabel = category;
            activeProgressCurrent = current;
            activeProgressTotal = total;
            Console.Out.Flush();
        }
    }

    public static void CompleteProgress(string category, int total)
    {
        if (total <= 0)
        {
            EndProgress();
            return;
        }

        lock (ConsoleLock)
        {
            if (progressActive &&
                string.Equals(activeProgressLabel, category, StringComparison.Ordinal) &&
                activeProgressTotal == total &&
                activeProgressCurrent >= total)
            {
                Console.WriteLine();
                ResetProgressState();
                return;
            }
        }

        Progress(category, total, total);
        EndProgress();
    }

    public static void EndProgress()
    {
        lock (ConsoleLock)
        {
            if (!progressActive) { return; }

            Console.WriteLine();
            ResetProgressState();
        }
    }

    private static string BuildProgressText(
        string category,
        int filled,
        double percent,
        int current,
        int total)
    {
        string bar = new string('=', filled);
        if (filled < ProgressWidth)
        {
            bar += ">" + new string('.', ProgressWidth - filled - 1);
        }

        string currentText = current.ToString("N0", CultureInfo.InvariantCulture);
        string totalText = total.ToString("N0", CultureInfo.InvariantCulture);
        string percentText = percent.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5);
        return $"[{category}] [{bar}] {percentText}% ({currentText}/{totalText})";
    }

    private static void ClearRemainder(int currentLength)
    {
        if (progressLineLength > currentLength)
        {
            Console.Write(new string(' ', progressLineLength - currentLength));
        }
    }

    private static void WriteLine(string prefix, string message, ConsoleColor? color)
    {
        lock (ConsoleLock)
        {
            if (progressActive)
            {
                Console.WriteLine();
                ResetProgressState();
            }

            string logMessage = (message ?? string.Empty).Trim(' ', '\t');

            if (color.HasValue)
            {
                Console.ForegroundColor = color.Value;
            }

            Console.Write(prefix);
            Console.ResetColor();

            if (logMessage.Length > 0)
            {
                Console.Write(' ');
                Console.Write(logMessage);
            }

            Console.WriteLine();
        }
    }

    private static void ResetProgressState()
    {
        progressActive = false;
        progressLineLength = 0;
        activeProgressLabel = null;
        activeProgressCurrent = 0;
        activeProgressTotal = 0;
    }
}
