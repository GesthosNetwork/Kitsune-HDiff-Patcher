using System.Reflection;

namespace Patcher;

internal static class EmbeddedTools
{
    private static readonly string ToolDir = Path.Combine(Path.GetTempPath(), "Kitsune", "tools");

    public static string Extract(string name)
    {
        Directory.CreateDirectory(ToolDir);
        string outputPath = Path.Combine(ToolDir, name);

        try
        {
            if (File.Exists(outputPath))
            {
                var info = new FileInfo(outputPath);
                if (info.Length > 0)
                {
                    return outputPath;
                }
            }
        }
        catch {}

        var assembly = Assembly.GetExecutingAssembly();

        string? resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith(name, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            throw new FileNotFoundException($"Embedded resource not found: {name}");
        }

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Embedded resource stream not found: {name}");
        }

        using var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.CopyTo(file);

        return outputPath;
    }
}
