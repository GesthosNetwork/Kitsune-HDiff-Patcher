using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Patcher;

internal static class Verify
{
    private const string UnlistedFilesDirectoryName = "Unlisted_Files";

    private static readonly HashSet<string> NonGameFiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "config.ini",
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

    public static bool Run(bool hasAppliedPatches, IReadOnlyList<string> processedArchives)
    {
        if (!hasAppliedPatches && processedArchives.Count == 0)
        {
            return true;
        }

        List<string> manifests = DiscoverManifests();

        if (manifests.Count == 0)
        {
            Program.SetTitle("Verification skipped");
            Logger.Warning("No matching package manifest found; verification skipped.");
            return true;
        }

        return RunManifests(manifests);
    }

    public static bool HasSupportedManifests()
    {
        return DiscoverManifests().Count > 0;
    }

    public static bool RunAll()
    {
        List<string> manifests = DiscoverManifests();

        if (manifests.Count == 0)
        {
            Program.SetTitle("Verification failed");
            Logger.Error("No supported package manifests found in the current directory.");
            return false;
        }

        return RunManifests(manifests);
    }

    private static bool RunManifests(List<string> manifests)
    {
        Program.SetTitle("Verifying files...");

        var failures = new List<string>();
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allExistingManifests = DiscoverManifests().ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool fullManifestCoverage = allExistingManifests.Count > 0 && allExistingManifests.SetEquals(manifests);
        var manifestEntries = new Dictionary<string, List<PkgEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (string manifest in manifests)
        {
            List<PkgEntry> entries = ReadEntries(manifest);
            manifestEntries[manifest] = entries;

            foreach (PkgEntry entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.RemoteName))
                {
                    expectedFiles.Add(NormalizePath(entry.RemoteName));
                }
            }
        }

        Logger.Info($"Found {manifests.Count} package manifest(s).");

        int total = 0;
        int ok = 0;
        int missing = 0;
        int sizeFail = 0;
        int md5Fail = 0;

        foreach (string manifest in manifests)
        {
            Logger.Info($"Processing {Path.GetFileName(manifest)} ...");
            VerifyStats stats = VerifyFile(manifest, manifestEntries[manifest], failures);
            total += stats.Total;
            ok += stats.Ok;
            missing += stats.Missing;
            sizeFail += stats.SizeFail;
            md5Fail += stats.Md5Fail;
        }

        List<string> unlistedFiles = fullManifestCoverage
            ? FindUnlistedFiles(expectedFiles)
            : new List<string>();
        if (failures.Count > 0 || unlistedFiles.Count > 0)
        {
            string reportName = $"verify_result_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

            File.WriteAllLines(
                reportName, BuildReport(manifests, failures, unlistedFiles, total, ok, missing, sizeFail, md5Fail, expectedFiles.Count),
                new UTF8Encoding(false));

            Logger.Info($"Verification report saved: {reportName}");
        }

        string unlistedSummary = fullManifestCoverage
            ? unlistedFiles.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            : "N/A";
        Logger.Info(
            $"Verification completed. Checked: {total:N0}; Passed: {ok:N0}; Missing: {missing:N0}; " +
            $"Size mismatch: {sizeFail:N0}; MD5 mismatch: {md5Fail:N0}; Unlisted: {unlistedSummary}.\n");

        bool unlistedMoveOk = fullManifestCoverage
            ? MoveUnlistedFiles(unlistedFiles)
            : true;
        bool fullyVerified = total > 0 &&
            ok == total &&
            missing == 0 &&
            sizeFail == 0 &&
            md5Fail == 0 &&
            failures.Count == 0;
        bool unlistedCleanupOk = true;

        if (fullyVerified && unlistedMoveOk)
        {
            unlistedCleanupOk = DeleteUnlistedFilesDirectory();
        }

        if (failures.Count > 0)
        {
            Program.SetTitle("Verification failed");
        }
        else if (!unlistedMoveOk || !unlistedCleanupOk)
        {
            Program.SetTitle("Verification cleanup failed");
        }
        else
        {
            Program.SetTitle("Completed successfully");
        }

        return failures.Count == 0 && unlistedMoveOk && unlistedCleanupOk;
    }

    private static List<string> DiscoverManifests()
    {
        string root = Directory.GetCurrentDirectory();
        var manifests = new List<string>();

        string defaultManifest = Path.Combine(root, "pkg_version");
        if (File.Exists(defaultManifest))
        {
            manifests.Add(defaultManifest);
        }

        manifests.AddRange(
            Directory.EnumerateFiles(root, "*_pkg_version", SearchOption.TopDirectoryOnly)
                .Where(IsValidManifestCandidate));

        return manifests
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(manifest => Path.GetFileName(manifest).Equals("pkg_version", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(manifest => Path.GetFileName(manifest), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsValidManifestCandidate(string path)
    {
        string fileName = Path.GetFileName(path);
        return fileName.EndsWith("_pkg_version", StringComparison.OrdinalIgnoreCase) &&
            fileName.Length > "_pkg_version".Length;
    }

    private static List<PkgEntry> ReadEntries(string manifest)
    {
        var entries = new List<PkgEntry>();

        foreach (string line in File.ReadLines(manifest, Encoding.UTF8))
        {
            PkgEntry? entry = ParseLine(line);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        return entries;
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
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to move unlisted file {relativePath}: {ex.Message}");
                success = false;
            }
        }

        if (!success)
        {
            Logger.Warning("One or more unlisted files could not be moved.");
        }

        return success;
    }

    private static bool DeleteUnlistedFilesDirectory()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), UnlistedFilesDirectoryName);
        if (!Directory.Exists(directory))
        {
            return true;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                EnsureWritableFile(file);
            }

            foreach (string subdirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            {
                EnsureWritableDirectory(subdirectory);
            }

            EnsureWritableDirectory(directory);
            Directory.Delete(directory, true);

            if (Directory.Exists(directory))
            {
                throw new IOException($"Directory still exists after deletion: {directory}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to remove {UnlistedFilesDirectoryName}: {ex.Message}");
            return false;
        }
    }

    private static void EnsureWritableDirectory(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch {}
    }

    private static bool IsManifestFileName(string fileName)
    {
        return fileName.Equals("pkg_version", StringComparison.OrdinalIgnoreCase) ||
            (fileName.EndsWith("_pkg_version", StringComparison.OrdinalIgnoreCase) &&
             fileName.Length > "_pkg_version".Length);
    }

    private static bool IsIgnoredUnlistedFile(string relativePath)
    {
        string normalized = NormalizePath(relativePath);
        string fileName = Path.GetFileName(relativePath);

        if (IsUnlistedFilesPath(normalized))
            return true;

        if (NonGameFiles.Contains(normalized))
            return true;

        if (IsManifestFileName(fileName))
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
        List<PkgEntry> entries,
        List<string> failures)
    {
        int total = entries.Count;
        int processed = 0;
        const string progressLabel = "VERIFY";
        int ok = 0;
        int missing = 0;
        int sizeFail = 0;
        int md5Fail = 0;

        if (total > 0)
        {
            Logger.Progress(progressLabel, 0, total);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            PkgEntry entry = entries[i];
            int index = i + 1;

            try
            {
                if (!File.Exists(entry.RemoteName))
                {
                    string message = $"{index}/{total} [MISSING] " + $"{entry.RemoteName}";
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

                    failures.Add($"[{pkgFile}] {message}");
                    md5Fail++;
                    continue;
                }

                ok++;
            }
            finally
            {
                processed++;
                Logger.Progress(progressLabel, processed, total);
            }
        }

        if (total > 0)
        {
            Logger.CompleteProgress(progressLabel, total);
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
