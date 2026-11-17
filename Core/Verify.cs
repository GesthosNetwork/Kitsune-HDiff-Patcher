using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Patcher;

internal static class Verify
{
    private static readonly string[] PkgFiles =
    [
        "pkg_version",
        "beyond_pkg_version",
        "Audio_English(US)_pkg_version",
        "Audio_Japanese_pkg_version",
        "Audio_Korean_pkg_version",
        "Audio_Chinese_pkg_version"
    ];

    private const string UnlistedFilesDirectoryName = "unlisted_files";

    private static readonly HashSet<string> NonGameFiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "config.ini",
            "Kitsune.exe",
            "LICENSE.txt",
            "vulkan_gpu_list_config.txt",
            "ThirdPartyNotices.txt",
            "desc.txt",
            "nameTranslation.txt",
            "AppIdentity.txt",
            "AudioLaucherRecord.txt",
            "DownloadedFullAssets.txt",
            "BeyondAssets/BeyondAssistEditor/Resource/Font/README.md"
        };

    public static bool Run()
    {
        Program.SetTitle("Verifying files...");

        var failures = new List<string>();
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifests = PkgFiles.Where(File.Exists).ToList();

        if (manifests.Count == 0)
        {
            Logger.Warning("No pkg_version files found.");
            Program.SetTitle("Verification skipped");
            return true;
        }

        foreach (string manifest in manifests)
            LoadExpectedFiles(manifest, expectedFiles);

        int total = 0;
        int ok = 0;
        int missing = 0;
        int sizeFail = 0;
        int md5Fail = 0;

        foreach (string manifest in manifests)
        {
            VerifyStats stats = VerifyFile(manifest, failures);
            total += stats.Total;
            ok += stats.Ok;
            missing += stats.Missing;
            sizeFail += stats.SizeFail;
            md5Fail += stats.Md5Fail;
        }

        List<string> unlistedFiles = FindUnlistedFiles(expectedFiles);
        string? reportName = null;

        if (failures.Count > 0 || unlistedFiles.Count > 0)
        {
            reportName = $"verify_result_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

            File.WriteAllLines(
                reportName, BuildReport(manifests, failures, unlistedFiles, total, ok, missing, sizeFail, md5Fail, expectedFiles.Count),
                new UTF8Encoding(false));

            Logger.Info($"Verify report saved to: {reportName}");
        }

        if (failures.Count > 0)
        {
            Program.SetTitle("Verification failed");
        }
        else if (unlistedFiles.Count > 0)
        {
            Program.SetTitle("Completed with unlisted files");
        }
        else
        {
            Program.SetTitle("Completed successfully");
        }

        Logger.Info("---------- SUMMARY ----------");
        Logger.Info($"Total         : {total}");
        Logger.Info($"OK            : {ok}");
        Logger.Info($"MISSING       : {missing}");
        Logger.Info($"SIZE FAIL     : {sizeFail}");
        Logger.Info($"MD5 FAIL      : {md5Fail}");
        Logger.Info($"UNLISTED      : {unlistedFiles.Count}");

        bool unlistedMoveOk = MoveUnlistedFiles(unlistedFiles);

        return failures.Count == 0 && unlistedMoveOk;
    }

    private static void LoadExpectedFiles(string manifest, HashSet<string> expectedFiles)
    {
        foreach (string line in File.ReadLines(manifest, Encoding.UTF8))
        {
            PkgEntry? entry = ParseLine(line);

            if (entry == null || string.IsNullOrWhiteSpace(entry.RemoteName))
            {
                continue;
            }

            expectedFiles.Add(NormalizePath(entry.RemoteName));
        }
    }

    private static List<string> FindUnlistedFiles(HashSet<string> expectedFiles)
    {
        string root = Path.GetFullPath(Directory.GetCurrentDirectory());
        var unlistedFiles = new List<string>();

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relativePath;

            try
            {
                relativePath = Path.GetRelativePath(root, file);
            }
            catch
            {
                continue;
            }

            if (IsIgnoredUnlistedFile(relativePath))
                continue;

            string normalized = NormalizePath(relativePath);

            if (!expectedFiles.Contains(normalized))
                unlistedFiles.Add(normalized);
        }

        unlistedFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return unlistedFiles;
    }

    private static bool IsUnlistedFilesPath(string normalizedPath)
    {
        return normalizedPath.Equals(UnlistedFilesDirectoryName, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(UnlistedFilesDirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MoveUnlistedFiles(List<string> unlistedFiles)
    {
        if (unlistedFiles.Count == 0)
            return true;

        string root = Path.GetFullPath(Directory.GetCurrentDirectory());
        string destinationRoot = Path.Combine(root, UnlistedFilesDirectoryName);
        bool success = true;

        try
        {
            Directory.CreateDirectory(destinationRoot);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to create {UnlistedFilesDirectoryName} directory: {ex.Message}");
            return false;
        }

        Logger.Info($"Moving {unlistedFiles.Count} unlisted file(s) to {UnlistedFilesDirectoryName}...");

        foreach (string relativePath in unlistedFiles)
        {
            string relativeOsPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            string source = Path.GetFullPath(Path.Combine(root, relativeOsPath));
            string destination = Path.Combine(destinationRoot, relativeOsPath);

            try
            {
                if (!File.Exists(source))
                {
                    Logger.Warning($"Unlisted file no longer exists: {relativePath}");
                    success = false;
                    continue;
                }

                string? destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                EnsureWritableFile(source);
                if (File.Exists(destination))
                {
                    EnsureWritableFile(destination);
                }

                File.Move(source, destination, true);
                Logger.Info($"Moved unlisted file: {relativePath} -> {UnlistedFilesDirectoryName}/{relativePath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to move unlisted file {relativePath}: {ex.Message}");
                success = false;
            }
        }

        if (success)
        {
            Logger.Info("Unlisted files moved successfully.");
        }
        else
        {
            Logger.Warning("One or more unlisted files could not be moved.");
        }

        return success;
    }

    private static bool IsIgnoredUnlistedFile(string relativePath)
    {
        string normalized = NormalizePath(relativePath);
        string fileName = Path.GetFileName(relativePath);

        if (IsUnlistedFilesPath(normalized))
            return true;

        if (NonGameFiles.Contains(normalized))
            return true;

        if (PkgFiles.Any(x => string.Equals(x, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string? processName = Path.GetFileName(Environment.ProcessPath);

        if (!string.IsNullOrEmpty(processName) &&
            string.Equals(fileName, processName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("verify_result_", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.EndsWith(".hash", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsGameFolderPersistentPath(normalized))
            return true;

        return IsPatchArtifact(fileName);
    }

    private static bool IsGameFolderPersistentPath(string normalizedPath)
    {
        const string persistentFolder = "Persistent";

        foreach (string gameFolder in GetGameFolders())
        {
            string prefix = $"{gameFolder}/{persistentFolder}";

            if (normalizedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetGameFolders()
    {
        string root = Path.GetFullPath(Directory.GetCurrentDirectory());

        foreach (string directory in Directory.EnumerateDirectories(root, "*_Data", SearchOption.TopDirectoryOnly))
        {
            yield return NormalizePath(Path.GetFileName(directory));
        }
    }

    private static bool IsPatchArtifact(string fileName)
    {
        string lower = fileName.ToLowerInvariant();
        return lower.EndsWith(".zip") || lower.EndsWith(".7z") || lower.EndsWith(".rar") ||
               Regex.IsMatch(lower, @"\.(zip|7z|rar)\.\d+$") || Regex.IsMatch(lower, @"\.part\d+\.rar$");
    }

    private static List<string> BuildReport(
        List<string> manifests, List<string> failures, List<string> unlistedFiles,
        int total, int ok, int missing, int sizeFail, int md5Fail, int expectedFileCount)
    {
        var report = new List<string>
        {
            "Kitsune HDiff Patcher - Verify Report",
            $"Generated       : {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            ""
        };

        report.Add("---------- MANIFEST FILES ----------");

        foreach (string manifest in manifests)
            report.Add($"[MANIFEST] {NormalizePath(manifest)}");

        report.Add($"Total manifest files: {manifests.Count}");
        report.Add("");

        report.Add("---------- SUMMARY ----------");
        report.Add($"Total         : {total}");
        report.Add($"OK            : {ok}");
        report.Add($"MISSING       : {missing}");
        report.Add($"SIZE FAIL     : {sizeFail}");
        report.Add($"MD5 FAIL      : {md5Fail}");
        report.Add($"UNLISTED      : {unlistedFiles.Count}");
        report.Add($"Expected files: {expectedFileCount}");
        report.Add("");

        if (unlistedFiles.Count > 0)
        {
            report.Add("---------- UNLISTED FILES ----------");

            foreach (string file in unlistedFiles)
                report.Add($"[UNLISTED] {file}");

            report.Add("");
        }

        report.Add("---------- FAILED CHECKS ----------");

        if (failures.Count == 0)
            report.Add("None");
        else
            report.AddRange(failures);

        return report;
    }

    private static VerifyStats VerifyFile(
        string pkgFile,
        List<string> failures)
    {
        var entries = new List<PkgEntry>();

        foreach (string line in File.ReadLines(pkgFile, Encoding.UTF8))
        {
            PkgEntry? entry = ParseLine(line);

            if (entry != null)
                entries.Add(entry);
        }

        int total = entries.Count;
        int ok = 0;
        int missing = 0;
        int sizeFail = 0;
        int md5Fail = 0;

        Logger.Info($"Processing {pkgFile} ...");

        for (int i = 0; i < entries.Count; i++)
        {
            PkgEntry entry = entries[i];
            int index = i + 1;

            if (!File.Exists(entry.RemoteName))
            {
                string message = $"{index}/{total} [MISSING] " + $"{entry.RemoteName}";
                Logger.Warning(message);
                failures.Add($"[{pkgFile}] {message}");
                missing++;
                continue;
            }

            long actualSize = new FileInfo(entry.RemoteName).Length;

            if (actualSize != entry.FileSize)
            {
                long diff = actualSize - entry.FileSize;

                string message =
                    $"{index}/{total} [SIZE FAIL] " +
                    $"{entry.RemoteName} | " +
                    $"expected={FormatSize(entry.FileSize)} " +
                    $"got={FormatSize(actualSize)} " +
                    $"diff={diff:+#;-#;+0} bytes";

                Logger.Warning(message);
                failures.Add($"[{pkgFile}] {message}");
                sizeFail++;
                continue;
            }

            string actualMd5 = CalculateMd5(entry.RemoteName);

            if (!string.Equals(actualMd5, entry.Md5, StringComparison.OrdinalIgnoreCase))
            {
                string message =
                    $"{index}/{total} [MD5 FAIL] " +
                    $"{entry.RemoteName} | " +
                    $"{FormatSize(actualSize)}";

                Logger.Warning(message);
                failures.Add($"[{pkgFile}] {message}");
                md5Fail++;
                continue;
            }

            Logger.Info(
                $"{index}/{total} [OK] " +
                $"{entry.RemoteName} | " +
                $"{FormatSize(actualSize)}");

            ok++;
        }

        return new VerifyStats(total, ok, missing, sizeFail, md5Fail);
    }

    private static void EnsureWritableFile(string path)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch {}
    }

    private static string CalculateMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string FormatSize(long size)
    {
        double mb = size / 1024d / 1024d;
        return $"{mb:0.000} MB ({size} bytes)";
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('.', '/');
    }

    private static PkgEntry? ParseLine(string line)
    {
        line = line.Trim();

        if (string.IsNullOrWhiteSpace(line))
            return null;

        if (line.StartsWith("{", StringComparison.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            return new PkgEntry(
                root.GetProperty("remoteName").GetString() ?? string.Empty,
                root.GetProperty("md5").GetString() ?? string.Empty,
                root.GetProperty("fileSize").GetInt64());
        }

        int lastSpace = line.LastIndexOf(' ');

        if (lastSpace < 0)
        {
            throw new FormatException($"Invalid line format: {line}");
        }

        string pathPart = line[..lastSpace].Trim();
        string rest = line[(lastSpace + 1)..].Trim();
        int pipeIndex = rest.IndexOf('|');

        if (pipeIndex < 0)
        {
            throw new FormatException($"Invalid line format: {line}");
        }

        string md5Part = rest[..pipeIndex].Trim();
        string sizePart = rest[(pipeIndex + 1)..].Trim();
        long size = long.Parse(sizePart, System.Globalization.CultureInfo.InvariantCulture);

        return new PkgEntry(pathPart, md5Part, size);
    }

    private sealed record VerifyStats(int Total, int Ok, int Missing, int SizeFail, int Md5Fail);
    private sealed record PkgEntry(string RemoteName, string Md5, long FileSize);
}
