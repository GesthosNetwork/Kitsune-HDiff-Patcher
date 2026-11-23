namespace Patcher;

internal static class Progress
{
    private const int BarWidth = 36;
    private static readonly object Sync = new();
    private static bool _lineActive;
    private static int _lastLineLength;

    public static void Report(string stage, int completed, int total, string? detail = null)
    {
        double percent = total <= 0
            ? 100d
            : Math.Clamp(completed * 100d / total, 0d, 100d);

        ReportPercent(stage, percent, total <= 0 ? "Complete" : $"{completed:N0}/{total:N0}");
    }

    public static void ReportPercent(string stage, double percent, string? count = null, string? detail = null)
    {
        percent = Math.Clamp(percent, 0d, 100d);
        int filled = Math.Clamp((int)Math.Floor(BarWidth * percent / 100d), 0, BarWidth);
        string completed = new('=', filled);
        bool showPointer = filled < BarWidth;
        string pointer = showPointer ? ">" : string.Empty;
        int remainingCount = BarWidth - filled - (showPointer ? 1 : 0);
        string remaining = new('.', remainingCount);
        string line = $"[{stage}] [{completed}{pointer}{remaining}] {percent,5:0.0}%";

        if (!string.IsNullOrWhiteSpace(count))
            line += $" ({count})";

        lock (Sync)
        {
            try
            {
                Console.Write('\r');
                Console.Write('[' + stage + "] [");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(completed + pointer);
                Console.ResetColor();
                Console.Write(remaining);
                Console.Write("] " + $"{percent,5:0.0}%");
                if (!string.IsNullOrWhiteSpace(count))
                    Console.Write($" ({count})");

                int padding = Math.Max(0, _lastLineLength - line.Length);
                if (padding > 0)
                    Console.Write(new string(' ', padding));

                _lastLineLength = line.Length;
                _lineActive = true;
            }
            catch {}
            finally
            {
                try { Console.ResetColor(); } catch {}
            }
        }

        Program.SetTitle($"{stage} {percent:0.0}%");
    }

    public static void WriteLine(string message = "")
    {
        lock (Sync)
        {
            try
            {
                if (_lineActive)
                {
                    Console.WriteLine();
                    _lineActive = false;
                    _lastLineLength = 0;
                }

                Console.WriteLine(message);
            }
            catch {}
        }
    }

    public static void End()
    {
        lock (Sync)
        {
            if (!_lineActive)
                return;

            try { Console.WriteLine(); }
            catch {}
            _lineActive = false;
            _lastLineLength = 0;
        }
    }
}
