using System.Reflection;
using System.Linq;
using System.Text;

namespace Patcher;

internal static class Program
{
    internal static string AppVersion { get; private set; } = "unknown";
    private static string ExecutableFileName =>
        $"{Assembly.GetExecutingAssembly().GetName().Name ?? AppDomain.CurrentDomain.FriendlyName}.exe";

    private static string Product =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? string.Empty;

    private static string AssemblyTitle =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>()?.Title
        ?? string.Empty;

    private static string Copyright =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? string.Empty;

    static int Main(string[] args)
    {
        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "unknown";

        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (args.Any(arg => IsArgument(arg, "--help", "-h", "/?")))
        {
            PrintHelp();
            return 0;
        }

        var supportedArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--verify", "-v", "--no-pause"
        };
        string[] unknownArguments = args
            .Where(arg => !supportedArguments.Contains(arg))
            .ToArray();

        if (unknownArguments.Length > 0)
        {
            Console.Error.WriteLine($"[ERROR] Unknown argument(s): {string.Join(", ", unknownArguments)}");
            Console.Error.WriteLine($"[INFO] Run '{ExecutableFileName} --help' to see available options.");
            return 2;
        }

        bool verifyOnly = args.Any(arg => IsArgument(arg, "--verify", "-v"));
        bool pauseOnExit = args.Length == 0;
        int result = 1;

        try
        {
            Console.WriteLine(Product);
            Console.WriteLine(AssemblyTitle);
            Console.WriteLine(Copyright);
            Console.WriteLine(new string('-', 52));
            Console.WriteLine();

            bool autoVerifyOnly = !verifyOnly && Verify.HasSupportedManifests() && !HasPatchInputs();

            if (verifyOnly || autoVerifyOnly)
            {
                Logger.Info("Verify-only mode enabled.");
                result = Verify.RunAll() ? 0 : 1;
            }
            else
            {
                result = Patch.Run(args);

                if (result == 0 && Patch.HasAppliedPatches)
                {
                    Logger.Info("Verifying file integrity...");
                    result = Verify.Run(Patch.HasAppliedPatches, Patch.ProcessedArchives) ? 0 : 1;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Unhandled exception: {ex.Message}");
            result = 1;
        }
        finally
        {
            Logger.EndProgress();
            if (result == 0)
            {
                Logger.Success("All operations completed successfully.");
            }
            else
            {
                Logger.Error("Operation completed with errors.");
            }

            if (pauseOnExit && !Console.IsInputRedirected)
            {
                Console.Write("\nPress any key to exit...");
                try
                {
                    Console.ReadKey(true);
                }
                catch (InvalidOperationException) {}
            }
        }

        return result;
    }

    private static bool IsArgument(string argument, params string[] names)
    {
        return names.Any(name => argument.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void PrintHelp()
    {
        Console.WriteLine($@"{Product} v{AppVersion}

        Usage:
          {ExecutableFileName} [options]

        Options:
          -h, --help      Show this help message and exit.
          -v, --verify    Verify files using supported package manifests without patching.
              --no-pause  Run the normal patch process without waiting for a key at exit.

        Run without options to extract update archives, apply HDiff patches, and verify
        updated files. Verification-only mode is selected automatically when no patch
        inputs are found and a supported package manifest is present.

        Working directory:
          Kitsune uses the current working directory. Run it from the game folder that
          contains the game files and update archives; it does not accept input paths.

        Exit codes:
          0  Operation completed successfully.
          1  Patching or file verification failed.
          2  An unsupported command-line argument was supplied.
        ");
    }

    private static bool HasPatchInputs()
    {
        string root = Directory.GetCurrentDirectory();

        string[] archivePatterns = ["*.zip", "*.7z", "*.rar"];
        if (archivePatterns.Any(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any()))
        {
            return true;
        }

        string[] multipartPatterns = ["*.zip.001", "*.7z.001", "*.rar.001", "*.part1.rar"];
        if (multipartPatterns.Any(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any()))
        {
            return true;
        }

        if (File.Exists("hdiffmap.json") || File.Exists("hdifffiles.txt"))
        {
            return true;
        }

        return Directory.EnumerateFiles(root, "*.hdiff", SearchOption.AllDirectories).Any();
    }

    internal static void SetTitle(string status)
    {
        try
        {
            Console.Title = $"{Product} v{AppVersion} - {status}";
        }
        catch {}
    }
}
