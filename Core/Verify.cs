using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Patcher;

internal static class Verify
{
    private static readonly string[] PKG_FILES =
    [
        "pkg_version",
        "beyond_pkg_version",
        "Audio_English(US)_pkg_version",
        "Audio_Japanese_pkg_version",
        "Audio_Korean_pkg_version",
        "Audio_Chinese_pkg_version"
    ];

    public static bool Run()
    {
        var failLogs = new List<string>();
        bool foundAny = false;

        foreach (var pkg in PKG_FILES)
        {
            if (File.Exists(pkg))
            {
                foundAny = true;
                VerifyFile(pkg, failLogs);
            }
        }

        if (!foundAny)
        {
            Logger.Warning("No pkg_version files found.");
            return true;
        }

        if (failLogs.Count > 0)
        {
            string logName = $"verify_result_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            File.WriteAllLines(logName, failLogs, new UTF8Encoding(false));
            Logger.Warning($"Fail report saved to: {logName}");
            return false;
        }

        Logger.Success("All files OK.");
        return true;
    }

    private static void VerifyFile(string pkgFile, List<string> failLogs)
    {
        var entries = new List<PkgEntry>();

        foreach (var line in File.ReadLines(pkgFile, Encoding.UTF8))
        {
            var parsed = ParseLine(line);
            if (parsed != null)
            {
                entries.Add(parsed);
            }
        }

        int total = entries.Count;
        int ok = 0;
        int missing = 0;
        int sizeMismatch = 0;
        int md5Mismatch = 0;

        Logger.Info($"Processing {pkgFile} ...");

        for (int i = 0; i < entries.Count; i++)
        {
            var data = entries[i];
            int index = i + 1;

            string remoteName = data.RemoteName;
            string expectedMd5 = data.Md5;
            long expectedSize = data.FileSize;

            if (!File.Exists(remoteName))
            {
                string text = $"{index}/{total} [MISSING] {remoteName}";
                Logger.Warning(text);
                failLogs.Add($"[{pkgFile}] {text}");
                missing++;
                continue;
            }

            long actualSize = new FileInfo(remoteName).Length;

            if (actualSize != expectedSize)
            {
                long diff = actualSize - expectedSize;
                string text =
                    $"{index}/{total} [SIZE FAIL] {remoteName} | " +
                    $"expected={FormatSize(expectedSize)} " +
                    $"got={FormatSize(actualSize)} " +
                    $"diff={diff:+#;-#;+0} bytes";

                Logger.Warning(text);
                failLogs.Add($"[{pkgFile}] {text}");
                sizeMismatch++;
                continue;
            }

            string actualMd5 = CalculateMd5(remoteName);
            if (!string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                string text = $"{index}/{total} [MD5 FAIL] {remoteName} | {FormatSize(actualSize)}";
                Logger.Warning(text);
                failLogs.Add($"[{pkgFile}] {text}");
                md5Mismatch++;
                continue;
            }

            Logger.Info($"{index}/{total} [OK] {remoteName} | {FormatSize(actualSize)}");
            ok++;
        }

        Logger.Info(string.Join(Environment.NewLine,
        [
            "---------- SUMMARY ----------",
            $"Total         : {total}",
            $"OK            : {ok}",
            $"MISSING       : {missing}",
            $"SIZE FAIL     : {sizeMismatch}",
            $"MD5 FAIL      : {md5Mismatch}"
        ]));
    }

    private static string CalculateMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);

        byte[] hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string FormatSize(long size)
    {
        double mb = size / 1024d / 1024d;
        return $"{mb:0.000} MB ({size} bytes)";
    }

    private static PkgEntry? ParseLine(string line)
    {
        line = line.Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (line.StartsWith("{", StringComparison.Ordinal))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            return new PkgEntry(
                root.GetProperty("remoteName").GetString() ?? string.Empty,
                root.GetProperty("md5").GetString() ?? string.Empty,
                root.GetProperty("fileSize").GetInt64()
            );
        }

        try
        {
            int lastSpace = line.LastIndexOf(' ');
            if (lastSpace < 0)
            {
                throw new FormatException();
            }

            string pathPart = line[..lastSpace].Trim();
            string rest = line[(lastSpace + 1)..].Trim();

            int pipeIndex = rest.IndexOf('|');
            if (pipeIndex < 0)
            {
                throw new FormatException();
            }

            string md5Part = rest[..pipeIndex].Trim();
            string sizePart = rest[(pipeIndex + 1)..].Trim();

            long size = long.Parse(sizePart, System.Globalization.CultureInfo.InvariantCulture);

            return new PkgEntry(pathPart, md5Part, size);
        }
        catch (Exception ex)
        {
            throw new FormatException($"Invalid line format: {line}", ex);
        }
    }

    private sealed record PkgEntry(string RemoteName, string Md5, long FileSize);
}
