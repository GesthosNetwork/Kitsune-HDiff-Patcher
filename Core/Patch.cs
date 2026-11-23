using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Patcher;

internal static class Patch
{
    private static readonly string[] GAME_FOLDERS =
    [
        "GenshinImpact_Data",
        "Genshin_Data",
        "YuanShen_Data",
        "StarRail_Data",
        "ZenlessZoneZero_Data",
        "ZenlessZoneZeroBeta_Data",
        "BH3_Data",
        "NexusAnima_Data",
        "Client"
    ];

    private static readonly HashSet<string> EXCLUDE_FILES =
    [
        "LICENSE.txt",
        "vulkan_gpu_list_config.txt",
        "ThirdPartyNotices.txt",
        "desc.txt",
        "nameTranslation.txt",
        "AppIdentity.txt",
        "AudioLaucherRecord.txt",
        "DownloadedFullAssets.txt",
        "BeyondAssets/BeyondAssistEditor/Resource/Font/README.md"
    ];

    private static readonly HashSet<string> EXCLUDE_FILES_LOWER =
        EXCLUDE_FILES.Select(NormalizeText).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex VERSION_REGEX =
        new(@"(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static readonly Regex VERSION_PAIR_REGEX =
        new(@"_(\d+\.\d+(?:\.\d+)?)_(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static readonly Regex MULTIPART_FIRST_001_REGEX =
        new(@"\.(7z|zip|rar)\.0*1$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MULTIPART_PART1_RAR_REGEX =
        new(@"\.part1\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MULTIPART_ANY_PART_REGEX =
        new(@"\.(7z|zip|rar)\.0*\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MULTIPART_ANY_RAR_PART_REGEX =
        new(@"\.part\d+\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SEVEN_ZIP_PERCENT_REGEX =
        new(@"^\s*(\d{1,3})%", RegexOptions.Compiled);

    private static bool pending_delete_for_migration = false;

    private static readonly HashSet<string> deleteList = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> processedArchives = new();

    public static IReadOnlyList<string> ProcessedArchives => processedArchives;
    public static bool HasAppliedPatches { get; private set; }

    private static string? HPATCHZ_PATH;
    private static string? SEVEN_ZIP_PATH;

    public static int Run(string[] args)
    {
        try
        {
            deleteList.Clear();
            processedArchives.Clear();
            pending_delete_for_migration = false;
            HasAppliedPatches = false;

            Program.SetTitle("Detecting game folder...");
            var gameFolder = DetectGameFolder();
            Logger.Info($"Game folder: {gameFolder.Name}");

            Program.SetTitle("Preparing tools...");
            CheckTools();

            bool patch_done = false;

            var cwd = Directory.GetCurrentDirectory();
            var multipartArchives = GetMultipartFirstParts();
            var candidates = new List<string>();
            candidates.AddRange(Directory.GetFiles(cwd, "*.zip", SearchOption.TopDirectoryOnly));
            candidates.AddRange(Directory.GetFiles(cwd, "*.7z", SearchOption.TopDirectoryOnly));
            candidates.AddRange(Directory.GetFiles(cwd, "*.rar", SearchOption.TopDirectoryOnly));

            var filtered = candidates
                .Select(Path.GetFileName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Where(x => !IsPartFileName(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int totalArchives = multipartArchives.Count + filtered.Count;
            Logger.Info(totalArchives == 0
                ? "No update archives found."
                : $"Found {totalArchives} update archive(s).");

            int archiveIndex = 0;
            foreach (FileInfo first in multipartArchives)
            {
                archiveIndex++;
                Program.SetTitle($"Processing {ShortTitleText(first.Name)}");
                Logger.Archive(LogicalNameFromFirst(first), archiveIndex, totalArchives);

                try
                {
                    bool archivedPatched = ExtractMultipartAndProcess(first, gameFolder);
                    patch_done |= archivedPatched;
                    if (archivedPatched)
                    {
                        processedArchives.Add(LogicalNameFromFirst(first));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error processing multipart {first.Name}: {ex.Message}");
                }
            }

            foreach (string archive_name in filtered)
            {
                archiveIndex++;
                var archive_path = new FileInfo(archive_name);
                Program.SetTitle($"Processing {ShortTitleText(archive_name)}");
                Logger.Archive(archive_name, archiveIndex, totalArchives);

                try
                {
                    ExtractSingleArchive(archive_path);
                    bool archivedPatched = ProcessLogicalArchive(archive_name, gameFolder);
                    patch_done |= archivedPatched;
                    if (archivedPatched)
                    {
                        processedArchives.Add(archive_name);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error processing archive {archive_name}: {ex.Message}");
                    throw;
                }
            }

            Program.SetTitle("Processing remaining HDiff patches...");
            if (HasHDiffInputs())
            {
                patch_done |= ApplyHDiff();
            }

            if (patch_done && pending_delete_for_migration)
            {
                Program.SetTitle("Deleting obsolete files...");
                DeleteFiles();
            }

            Program.SetTitle("Cleaning temporary files...");
            if (!CleanupAuxFiles(gameFolder))
            {
                throw new IOException("Cleanup did not complete successfully; verification was not started.");
            }

            Program.SetTitle("Removing empty directories...");
            CleanupEmptyDirs(gameFolder);
            CleanupEmptyDirsRoot();
            Logger.Info("Cleanup completed.\n");

            return 0;
        }
        catch (Exception ex)
        {
            Program.SetTitle("Failed");
            Logger.Error(ex.Message);
            return 1;
        }
    }

    private static bool HasHDiffInputs()
    {
        if (File.Exists("hdifffiles.txt") || File.Exists("hdiffmap.json"))
        {
            return true;
        }

        return Directory.EnumerateFiles(
            Directory.GetCurrentDirectory(), "*.hdiff", SearchOption.AllDirectories).Any();
    }

    private static string NormalizeText(string value)
    {
        return value
            .Replace('\\', '/').ToLowerInvariant().TrimStart('.', '/');
    }

    private static string NormalizeVersion(string v)
    {
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            return $"{parts[0]}.{parts[1]}.0";
        }

        return v;
    }

    private static bool IsVersionLess(int major, int minor, int targetMajor, int targetMinor)
    {
        return major < targetMajor || (major == targetMajor && minor < targetMinor);
    }

    private static bool IsVersionAtLeast(int major, int minor, int targetMajor, int targetMinor)
    {
        return major > targetMajor || (major == targetMajor && minor >= targetMinor);
    }

    private static int[] ParseVersionParts(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();

        if (parts.Length == 0 || parts.Length > 3)
        {
            throw new FormatException($"Invalid version: {version}");
        }

        return [.. parts, .. Enumerable.Repeat(0, 3 - parts.Length)];
    }

    private static int CompareVersions(string left, string right)
    {
        var a = ParseVersionParts(left);
        var b = ParseVersionParts(right);

        for (int i = 0; i < 3; i++)
        {
            int comparison = a[i].CompareTo(b[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool SupportsAudioMigration(DirectoryInfo gameFolder)
    {
        return gameFolder.Name.Equals("GenshinImpact_Data", StringComparison.OrdinalIgnoreCase)
            || gameFolder.Name.Equals("Genshin_Data", StringComparison.OrdinalIgnoreCase)
            || gameFolder.Name.Equals("YuanShen_Data", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathText(string path)
    {
        try
        {
            return NormalizeText(Path.GetFullPath(path));
        }
        catch
        {
            return NormalizeText(path);
        }
    }

    private static string ShortTitleText(string text, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 3)] + "...";
    }

    private static void SetFileTitle(string action, string path)
    {
        Program.SetTitle($"{action} {ShortTitleText(path)}");
    }

    private static bool IsExcluded(FileSystemInfo path)
    {
        string name_lower = path.Name.ToLowerInvariant();
        if (name_lower.EndsWith(".license.txt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string candidate_text = NormalizePathText(path.FullName);

        if (EXCLUDE_FILES_LOWER.Contains(candidate_text))
        {
            return true;
        }

        foreach (var excluded in EXCLUDE_FILES_LOWER)
        {
            if (candidate_text.EndsWith("/" + excluded, StringComparison.OrdinalIgnoreCase) ||
                candidate_text == excluded)
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureWritable(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch {}
    }

    private static void MakeWritableRecursive(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        if (File.Exists(path))
        {
            EnsureWritable(path);
            return;
        }

        try
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    EnsureWritable(item);
                }
                catch {}
            }
        }
        catch {}

        EnsureWritable(path);
    }

    private static (int ExitCode, string Output) RunProcess(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);

        if (process == null)
        {
            throw new Exception($"Failed start process: {exe}");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);

        string output = string.Join(
            Environment.NewLine,
            new[] { outputTask.Result, errorTask.Result }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        return (process.ExitCode, output);
    }

    private static string FormatProcessFailure(int exitCode, string output)
    {
        string detail = output
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (detail.Length > 1200)
        {
            detail = detail[^1200..];
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"hpatchz exit code {exitCode}"
            : $"hpatchz exit code {exitCode}: {detail}";
    }

    private sealed class ArchiveProgressTracker
    {
        private readonly object sync = new();
        private readonly int totalFiles;
        private int processedFiles;
        private double? sevenZipPercent;

        public ArchiveProgressTracker(int totalFiles)
        {
            this.totalFiles = totalFiles;
        }

        public void FileCompleted()
        {
            lock (sync)
            {
                if (processedFiles < totalFiles)
                {
                    processedFiles++;
                }

                double percent = sevenZipPercent ?? processedFiles / (double)totalFiles * 100d;
                Logger.ProgressPercent("ARCHIVE", percent, processedFiles, totalFiles);
            }
        }

        public void ReportSevenZipPercent(int percent)
        {
            lock (sync)
            {
                double nextPercent = Math.Clamp(percent, 0, 100);
                sevenZipPercent = Math.Max(sevenZipPercent ?? 0d, nextPercent);
                Logger.ProgressPercent("ARCHIVE", sevenZipPercent.Value, processedFiles, totalFiles);
            }
        }
    }

    private static int RunSevenZipProcess(string exe, IEnumerable<string> args, int totalFiles)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);

        if (process == null)
        {
            throw new Exception($"Failed start process: {exe}");
        }

        var progress = new ArchiveProgressTracker(totalFiles);
        Task fileProgressTask = ReadSevenZipFileOutputAsync(process.StandardOutput, progress);
        Task<string> errorTask = ReadSevenZipErrorAndProgressAsync(process.StandardError, progress);
        process.WaitForExit();

        Task.WaitAll(fileProgressTask, errorTask);
        string error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            string detail = error.Trim();
            if (detail.Length > 1200)
            {
                detail = detail[^1200..];
            }

            string message = $"7z extraction failed with exit code {process.ExitCode}";
            if (!string.IsNullOrWhiteSpace(detail))
            {
                message += $": {detail}";
            }

            throw new Exception(message);
        }

        if (totalFiles > 0)
        {
            Logger.CompleteProgress("ARCHIVE", totalFiles);
        }

        return process.ExitCode;
    }

    private static async Task ReadSevenZipFileOutputAsync(
        StreamReader reader,
        ArchiveProgressTracker progress)
    {
        var buffer = new char[512];
        var line = new StringBuilder();

        while (true)
        {
            int read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                char current = buffer[i];

                if (current is '\r' or '\n')
                {
                    CountExtractedFile(line.ToString(), progress);
                    line.Clear();
                }
                else if (!char.IsControl(current))
                {
                    line.Append(current);
                }
            }
        }

        if (line.Length > 0)
        {
            CountExtractedFile(line.ToString(), progress);
        }
    }

    private static async Task<string> ReadSevenZipErrorAndProgressAsync(
        StreamReader reader,
        ArchiveProgressTracker progress)
    {
        var buffer = new char[512];
        var line = new StringBuilder();
        var errorOutput = new StringBuilder();

        while (true)
        {
            int read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                char current = buffer[i];

                if (current is '\r' or '\n')
                {
                    ProcessSevenZipErrorLine(line.ToString(), progress, errorOutput);
                    line.Clear();
                }
                else if (!char.IsControl(current))
                {
                    line.Append(current);
                }
            }
        }

        if (line.Length > 0)
        {
            ProcessSevenZipErrorLine(line.ToString(), progress, errorOutput);
        }

        return errorOutput.ToString();
    }

    private static void ProcessSevenZipErrorLine(
        string outputLine,
        ArchiveProgressTracker progress,
        StringBuilder errorOutput)
    {
        string trimmed = outputLine.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        Match percentMatch = SEVEN_ZIP_PERCENT_REGEX.Match(trimmed);
        if (percentMatch.Success &&
            int.TryParse(percentMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int percent))
        {
            progress.ReportSevenZipPercent(percent);
            return;
        }

        errorOutput.AppendLine(trimmed);
    }

    private static void CountExtractedFile(string outputLine, ArchiveProgressTracker progress)
    {
        string trimmed = outputLine.TrimStart();
        if (!trimmed.StartsWith("+ ", StringComparison.Ordinal))
        {
            return;
        }

        progress.FileCompleted();
    }

    private static int GetArchiveFileCount(FileInfo archive)
    {
        string sevenZip = SEVEN_ZIP_PATH ?? throw new InvalidOperationException("7z.exe not loaded.");
        (int exitCode, string output) = RunProcess(sevenZip, new[] { "l", "-slt", archive.FullName });

        if (exitCode != 0)
        {
            string detail = output.Trim();
            if (detail.Length > 1200)
            {
                detail = detail[^1200..];
            }

            throw new Exception(string.IsNullOrWhiteSpace(detail)
                ? $"Unable to read archive file list: {archive.Name}"
                : $"Unable to read archive file list: {archive.Name}: {detail}");
        }

        int fileCount = 0;
        bool entriesStarted = false;
        bool hasPath = false;
        bool isDirectory = false;

        void CommitEntry()
        {
            if (hasPath && !isDirectory)
            {
                fileCount++;
            }

            hasPath = false;
            isDirectory = false;
        }

        using var stringReader = new StringReader(output);
        string? line;
        while ((line = stringReader.ReadLine()) != null)
        {
            if (!entriesStarted)
            {
                if (line.Trim() == "----------")
                {
                    entriesStarted = true;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                CommitEntry();
                continue;
            }

            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                hasPath = true;
            }
            else if (line.StartsWith("Folder = +", StringComparison.Ordinal))
            {
                isDirectory = true;
            }
            else if (line.StartsWith("Attributes = ", StringComparison.Ordinal))
            {
                string attributes = line["Attributes = ".Length..].TrimStart();
                if (attributes.StartsWith("D", StringComparison.OrdinalIgnoreCase))
                {
                    isDirectory = true;
                }
            }
        }

        CommitEntry();

        if (!entriesStarted)
        {
            throw new Exception($"Unrecognized 7z file listing format: {archive.Name}");
        }

        return fileCount;
    }

    private static void ReplaceTextInFile(string filepath)
    {
        if (!File.Exists(filepath))
        {
            return;
        }

        string text = File.ReadAllText(filepath, Encoding.UTF8);
        text = text.Replace("{\"remoteName\": \"", "").Replace("\"}", "").Replace("/", "\\");
        File.WriteAllText(filepath, text, new UTF8Encoding(false));
    }

    private static DirectoryInfo DetectGameFolder()
    {
        foreach (var folder in GAME_FOLDERS)
        {
            var dir = new DirectoryInfo(folder);
            if (dir.Exists)
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException($"No supported game folder found. Expected one of: {string.Join(", ", GAME_FOLDERS)}");
    }

    private static void CheckTools()
    {
        try
        {
            HPATCHZ_PATH = EmbeddedTools.Extract("hpatchz.exe");
            SEVEN_ZIP_PATH = EmbeddedTools.Extract("7z.exe");

            if (!File.Exists(HPATCHZ_PATH))
            {
                throw new FileNotFoundException("Failed extracting hpatchz.exe");
            }

            if (!File.Exists(SEVEN_ZIP_PATH))
            {
                throw new FileNotFoundException("Failed extracting 7z.exe");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Tool extraction failed: {ex.Message}", ex);
        }
    }

    private static void DeleteFiles()
    {
        Program.SetTitle("Deleting obsolete files...");

        string delete_txt = "deletefiles.txt";
        if (!File.Exists(delete_txt))
        {
            return;
        }

        ReplaceTextInFile(delete_txt);

        List<string> entries = File.ReadAllLines(delete_txt, Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        int total = entries.Count;
        int processed = 0;
        int deleted = 0;
        int notFound = 0;
        var failures = new List<string>();

        if (total > 0)
        {
            Logger.Progress("DELETE", 0, total);
        }

        foreach (string raw in entries)
        {
            try
            {
                deleteList.Add(NormalizePathText(raw));

                string target = raw;
                try
                {
                    target = Path.GetFullPath(raw);
                }
                catch {}

                SetFileTitle("Deleting", target);

                if (!File.Exists(target) && !Directory.Exists(target))
                {
                    notFound++;
                    continue;
                }

                MakeWritableRecursive(target);

                if (File.Exists(target))
                {
                    File.Delete(target);
                }
                else
                {
                    Directory.Delete(target, true);
                }

                deleted++;
            }
            catch (Exception ex)
            {
                failures.Add($"Failed to delete '{raw}': {ex.Message}");
            }
            finally
            {
                processed++;
                if (total > 0)
                {
                    Logger.Progress("DELETE", processed, total);
                }
            }
        }

        if (total > 0)
        {
            Logger.CompleteProgress("DELETE", total);
        }

        Logger.Info($"Obsolete-file processing completed. Deleted: {deleted}; Not found: {notFound}; Failed: {failures.Count}.");

        foreach (string failure in failures)
        {
            Logger.Warning(failure);
        }

        try
        {
            File.Delete(delete_txt);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to delete {delete_txt}: {ex.Message}");
        }
    }

    private static List<(FileInfo Source, FileInfo Patch, FileInfo Target)> ReadHdiffmapJson()
    {
        string hdiffmap = "hdiffmap.json";
        if (!File.Exists(hdiffmap))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(hdiffmap, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);

            var results = new List<(FileInfo, FileInfo, FileInfo)>();

            if (!doc.RootElement.TryGetProperty("diff_map", out var diffMap) ||
                diffMap.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var entry in diffMap.EnumerateArray())
            {
                string? source = entry.TryGetProperty("source_file_name", out var s) ? s.GetString() : null;
                string? patch = entry.TryGetProperty("patch_file_name", out var p) ? p.GetString() : null;
                string? target = entry.TryGetProperty("target_file_name", out var t) ? t.GetString() : null;

                if (string.IsNullOrWhiteSpace(source) ||
                    string.IsNullOrWhiteSpace(patch) ||
                    string.IsNullOrWhiteSpace(target))
                {
                    Logger.Warning($"Invalid diff_map entry: {entry}");
                    continue;
                }

                results.Add((new FileInfo(source), new FileInfo(patch), new FileInfo(target)));
            }

            return results;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to parse hdiffmap.json: {ex.Message}");
            return [];
        }
    }

    private static void UpdatePatchProgress(ref int processed, int total)
    {
        if (total <= 0)
        {
            return;
        }

        processed++;
        Logger.Progress("PATCH", processed, total);
    }

    private static bool ApplyHDiff()
    {
        Logger.Info("Processing HDiff patches...");
        bool hasHdiffFiles = File.Exists("hdifffiles.txt");
        bool hasHdiffMap = File.Exists("hdiffmap.json");

        if (!hasHdiffFiles && !hasHdiffMap)
        {
            return ApplyHDiffWithoutMapping();
        }

        string hdifffilesTxt = "hdifffiles.txt";
        var targets = new List<string>();

        if (hasHdiffFiles)
        {
            Logger.Info("HDiff map: hdifffiles.txt");
            ReplaceTextInFile(hdifffilesTxt);
            targets = File.ReadAllLines(hdifffilesTxt, Encoding.UTF8)
                .Select(line => line.Trim())
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .ToList();
        }

        List<(FileInfo Source, FileInfo Patch, FileInfo Target)> mapItems = hasHdiffMap
            ? ReadHdiffmapJson()
            : new List<(FileInfo Source, FileInfo Patch, FileInfo Target)>();

        if (hasHdiffMap)
        {
            Logger.Info("HDiff map: hdiffmap.json");
        }

        int total = targets.Count + mapItems.Count;
        int processed = 0;
        int applied = 0;
        int skipped = 0;
        int failed = 0;
        var warnings = new List<string>();

        if (total == 0)
        {
            Logger.Info("No HDiff patches found in the mapping files.");
            return false;
        }

        Logger.Progress("PATCH", 0, total);

        foreach (string target in targets)
        {
            string originalFile = target;
            string hdiff = $"{target}.hdiff";

            if (!File.Exists(originalFile))
            {
                skipped++;
                warnings.Add($"Target file not found: {originalFile}");
                UpdatePatchProgress(ref processed, total);
                continue;
            }

            if (!File.Exists(hdiff))
            {
                skipped++;
                warnings.Add($"Patch file not found: {hdiff}");
                UpdatePatchProgress(ref processed, total);
                continue;
            }

            EnsureWritable(originalFile);

            try
            {
                Program.SetTitle($"Patching {ShortTitleText(Path.GetFileName(originalFile))}");

                (int code, string output) = RunProcess(
                    HPATCHZ_PATH ?? throw new InvalidOperationException("hpatchz.exe not loaded."),
                    new[]
                    {
                        "-f",
                        Path.GetFullPath(originalFile),
                        Path.GetFullPath(hdiff),
                        Path.GetFullPath(originalFile)
                    });

                if (code != 0)
                {
                    throw new Exception(FormatProcessFailure(code, output));
                }

                applied++;
                HasAppliedPatches = true;
            }
            catch (Exception ex)
            {
                failed++;
                Logger.Error($"HDiff patching stopped. Applied: {applied}; Skipped: {skipped}; Failed: {failed}.");
                throw new Exception($"hpatchz failed for {originalFile}: {ex.Message}", ex);
            }

            try
            {
                File.Delete(hdiff);
            }
            catch {}

            UpdatePatchProgress(ref processed, total);
        }

        foreach (var item in mapItems)
        {
            var source = item.Source;
            var patch = item.Patch;
            var target = item.Target;

            if (!source.Exists)
            {
                skipped++;
                warnings.Add($"Source file not found: {source.FullName}");
                UpdatePatchProgress(ref processed, total);
                continue;
            }

            if (!patch.Exists)
            {
                skipped++;
                warnings.Add($"Patch file not found: {patch.FullName}");
                UpdatePatchProgress(ref processed, total);
                continue;
            }

            try
            {
                if (target.Directory != null)
                {
                    target.Directory.Create();
                }

                EnsureWritable(source.FullName);
                Program.SetTitle($"Patching {ShortTitleText(target.Name)}");

                (int code, string output) = RunProcess(
                    HPATCHZ_PATH ?? throw new InvalidOperationException("hpatchz.exe not loaded."),
                    new[] { "-f", source.FullName, patch.FullName, target.FullName });

                if (code != 0)
                {
                    throw new Exception(FormatProcessFailure(code, output));
                }

                applied++;
                HasAppliedPatches = true;

                if (!source.FullName.Equals(target.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        EnsureWritable(source.FullName);
                        File.Delete(source.FullName);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Failed to delete old source file {source.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                Logger.Error($"HDiff patching stopped. Applied: {applied}; Skipped: {skipped}; Failed: {failed}.");
                throw new Exception($"hpatchz failed for {source.FullName}: {ex.Message}", ex);
            }

            try
            {
                File.Delete(patch.FullName);
            }
            catch {}

            UpdatePatchProgress(ref processed, total);
        }

        Logger.CompleteProgress("PATCH", total);
        Logger.Info($"HDiff patching completed. Applied: {applied}; Skipped: {skipped}; Failed: {failed}.\n");

        foreach (string warning in warnings)
        {
            Logger.Warning(warning);
        }

        try
        {
            if (hasHdiffFiles && File.Exists(hdifffilesTxt))
            {
                File.Delete(hdifffilesTxt);
            }
        }
        catch {}

        try
        {
            if (hasHdiffMap && File.Exists("hdiffmap.json"))
            {
                File.Delete("hdiffmap.json");
            }
        }
        catch {}

        return applied > 0;
    }

    private static bool ApplyHDiffWithoutMapping()
    {
        bool patched = false;
        string root = Directory.GetCurrentDirectory();
        var hdiffFiles = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(x => x.EndsWith(".hdiff", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int total = hdiffFiles.Count;
        if (total == 0)
        {
            return false;
        }

        int pairCount = hdiffFiles.Count(hdiff => File.Exists(hdiff[..^".hdiff".Length]));
        Logger.Info("No mapping file found; using adjacent HDiff detection.");
        Logger.Info($"Found {pairCount} adjacent HDiff pair(s).");

        int processed = 0;
        int applied = 0;
        int skipped = 0;
        int failed = 0;
        var warnings = new List<string>();

        Logger.Progress("PATCH", 0, total);

        foreach (string hdiff in hdiffFiles)
        {
            string source = hdiff[..^".hdiff".Length];

            if (!File.Exists(source))
            {
                skipped++;
                warnings.Add($"Source file not found: {source}");
                UpdatePatchProgress(ref processed, total);
                continue;
            }

            EnsureWritable(source);

            try
            {
                Program.SetTitle($"Patching {ShortTitleText(Path.GetFileName(source))}");

                (int code, string output) = RunProcess(
                    HPATCHZ_PATH ?? throw new InvalidOperationException("hpatchz.exe not loaded."),
                    new[]
                    {
                        "-f", Path.GetFullPath(source), Path.GetFullPath(hdiff), Path.GetFullPath(source)
                    });

                if (code != 0)
                {
                    throw new Exception(FormatProcessFailure(code, output));
                }

                patched = true;
                applied++;
                HasAppliedPatches = true;
            }
            catch (Exception ex)
            {
                failed++;
                Logger.Error($"HDiff patching stopped. Applied: {applied}; Skipped: {skipped}; Failed: {failed}.");
                throw new Exception($"hpatchz failed for {source}: {ex.Message}", ex);
            }

            try
            {
                File.Delete(hdiff);
            }
            catch {}

            UpdatePatchProgress(ref processed, total);
        }

        Logger.CompleteProgress("PATCH", total);
        Logger.Info($"HDiff patching completed. Applied: {applied}; Skipped: {skipped}; Failed: {failed}.\n");

        foreach (string warning in warnings)
        {
            Logger.Warning(warning);
        }

        return patched;
    }

    private static void ExtractWith7z(FileInfo archive)
    {
        string sevenZip = SEVEN_ZIP_PATH ?? throw new InvalidOperationException("7z.exe not loaded.");
        int totalFiles = GetArchiveFileCount(archive);

        int code = RunSevenZipProcess(
            sevenZip,
            new[]
            {
                "x", archive.FullName, "-o.", "-y", "-bb1", "-bso1", "-bsp2"
            },
            totalFiles);

        if (code != 0)
        {
            throw new Exception($"7z extraction failed: {archive.Name}");
        }
    }

    private static bool IsMultipartFirst(FileInfo file)
    {
        string name = file.Name.ToLowerInvariant();

        if (MULTIPART_FIRST_001_REGEX.IsMatch(name))
        {
            return true;
        }

        if (MULTIPART_PART1_RAR_REGEX.IsMatch(name))
        {
            return true;
        }

        return false;
    }

    private static List<FileInfo> GetMultipartFirstParts()
    {
        var result = new List<FileInfo>();

        foreach (var file in Directory.GetFiles(Directory.GetCurrentDirectory()))
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                continue;
            }

            if (IsMultipartFirst(info))
            {
                result.Add(info);
            }
        }

        return result
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<FileInfo> CollectPartsForFirst(FileInfo first)
    {
        string name = first.Name;
        string lower = name.ToLowerInvariant();
        var result = new List<FileInfo>();

        var match = Regex.Match(lower, @"^(.*\.(?:7z|zip|rar))\.0*1$");
        if (match.Success)
        {
            string prefix = match.Groups[1].Value;

            foreach (var file in Directory.GetFiles(Directory.GetCurrentDirectory(), prefix + ".*"))
            {
                result.Add(new FileInfo(file));
            }

            return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (lower.EndsWith(".part1.rar", StringComparison.OrdinalIgnoreCase))
        {
            string prefix = name[..^".part1.rar".Length];

            foreach (var file in Directory.GetFiles(Directory.GetCurrentDirectory(), prefix + ".part*.rar"))
            {
                result.Add(new FileInfo(file));
            }

            return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        result.Add(first);
        return result;
    }

    private static string LogicalNameFromFirst(FileInfo first)
    {
        string name = first.Name;
        string lower = name.ToLowerInvariant();

        var match = Regex.Match(lower, @"^(.*\.(?:7z|zip|rar))\.0*1$");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        if (lower.EndsWith(".part1.rar", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return name;
    }

    private static bool ExtractMultipartAndProcess(FileInfo first, DirectoryInfo gameFolder)
    {
        string logical = LogicalNameFromFirst(first);

        try
        {
            Program.SetTitle($"Extracting {ShortTitleText(first.Name)}");
            ExtractWith7z(first);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to extract multipart {first}: {ex.Message}");
        }

        foreach (var part in CollectPartsForFirst(first))
        {
            try
            {
                if (!IsExcluded(part))
                {
                    part.Delete();
                }
            }
            catch {}
        }

        return ProcessLogicalArchive(logical, gameFolder);
    }

    private static bool IsPartFileName(string name)
    {
        string ln = name.ToLowerInvariant();

        if (MULTIPART_FIRST_001_REGEX.IsMatch(ln)) { return true; }
        if (MULTIPART_PART1_RAR_REGEX.IsMatch(ln)) { return true; }
        if (MULTIPART_ANY_PART_REGEX.IsMatch(ln)) { return true; }
        if (MULTIPART_ANY_RAR_PART_REGEX.IsMatch(ln)) { return true; }

        return false;
    }

    private static void ExtractSingleArchive(FileInfo archive)
    {
        try
        {
            Program.SetTitle($"Extracting {ShortTitleText(archive.Name)}");
            ExtractWith7z(archive);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to extract {archive}: {ex.Message}");
        }

        try
        {
            if (!IsExcluded(archive))
            {
                archive.Delete();
            }
        }
        catch {}
    }

    private static (string? From, string? To) ParseFromToVersionsFromName(string name)
    {
        var match = VERSION_PAIR_REGEX.Match(name);
        if (match.Success)
        {
            return (
                NormalizeVersion(match.Groups[1].Value),
                NormalizeVersion(match.Groups[2].Value)
            );
        }

        return (null, null);
    }

    private static bool MigrateAudioIfNeeded(DirectoryInfo gameFolder, string? versionFrom, string? versionTo)
    {
        if (versionTo == null || !SupportsAudioMigration(gameFolder))
        {
            return false;
        }

        Program.SetTitle("Migrating audio files...");

        try
        {
            if (CompareVersions(versionTo, "3.5.50") < 0)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        string oldPath = Path.Combine(gameFolder.FullName, "StreamingAssets", "Audio", "GeneratedSoundBanks", "Windows");
        string newPath = Path.Combine(gameFolder.FullName, "StreamingAssets", "AudioAssets");

        if (!Directory.Exists(oldPath))
        {
            return false;
        }

        Directory.CreateDirectory(newPath);
        Logger.Info("Migrating audio files...");

        string[] audioFiles = Directory.GetFiles(oldPath, "*", SearchOption.AllDirectories);
        int migrated = 0;
        int failed = 0;
        int processed = 0;

        if (audioFiles.Length > 0)
        {
            Logger.Progress("MIGRATE", 0, audioFiles.Length);
        }

        foreach (var file in audioFiles)
        {
            try
            {
                string relative = Path.GetRelativePath(oldPath, file);
                string destination = Path.Combine(newPath, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                try
                {
                    File.Move(file, destination, true);
                }
                catch
                {
                    File.Copy(file, destination, true);
                }

                migrated++;
            }
            catch
            {
                failed++;
            }
            finally
            {
                processed++;
                if (audioFiles.Length > 0)
                {
                    Logger.Progress("MIGRATE", processed, audioFiles.Length);
                }
            }
        }

        if (audioFiles.Length > 0)
        {
            Logger.CompleteProgress("MIGRATE", audioFiles.Length);
        }

        Logger.Info($"Audio migration completed. Migrated: {migrated}; Failed: {failed}.");
        if (failed > 0)
        {
            Logger.Warning($"{failed} audio file(s) could not be migrated.");
        }

        try
        {
            Directory.Delete(oldPath, true);
        }
        catch {}

        return true;
    }

    private static bool ProcessLogicalArchive(string archiveName, DirectoryInfo gameFolder)
    {
        bool patched = false;
        var versions = ParseFromToVersionsFromName(archiveName);

        bool needsMigration = false;

        if (SupportsAudioMigration(gameFolder) && versions.From != null && versions.To != null)
        {
            try
            {
                bool oldVersion = CompareVersions(versions.From, "3.5.50") < 0;
                bool newVersion = CompareVersions(versions.To, "3.5.50") >= 0;

                needsMigration = oldVersion && newVersion;
            }
            catch
            {
                needsMigration = false;
            }
        }

        if (needsMigration)
        {
            bool migrated = MigrateAudioIfNeeded(gameFolder, versions.From, versions.To);
            if (!migrated)
            {
                Logger.Warning("Migration indicated but did not complete; continuing to apply hdiff may fail.");
            }

            pending_delete_for_migration = true;
        }
        else
        {
            Program.SetTitle("Deleting obsolete files...");
            DeleteFiles();
        }

        Program.SetTitle("Applying patch...");
        if (HasHDiffInputs() && ApplyHDiff())
        {
            patched = true;
        }

        return patched;
    }

    private static void CleanupEmptyDirs(DirectoryInfo gameFolder)
    {
        while (true)
        {
            bool removed = false;

            var dirs = Directory
                .GetDirectories(gameFolder.FullName, "*", SearchOption.AllDirectories)
                .OrderByDescending(x => x.Length)
                .ToList();

            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                        removed = true;
                    }
                }
                catch {}
            }

            if (!removed)
            {
                break;
            }
        }
    }

    private static void CleanupEmptyDirsRoot()
    {
        string root = Directory.GetCurrentDirectory();

        while (true)
        {
            bool removed = false;

            foreach (var dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);

                if (GAME_FOLDERS.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                        removed = true;
                    }
                }
                catch {}
            }

            if (!removed)
            {
                break;
            }
        }
    }

    private static bool CleanupAuxFiles(DirectoryInfo gameFolder)
    {
        string root = Directory.GetCurrentDirectory();
        bool success = true;

        string[] exactFiles =
        [
            "deletefiles.txt",
            "hdifffiles.txt",
            "hdiffmap.json"
        ];

        foreach (string fileName in exactFiles)
        {
            string path = Path.Combine(root, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            if (!TryDeleteFile(path, "cleanup artifact"))
            {
                success = false;
            }
        }

        foreach (string file in Directory.EnumerateFiles(root, "*.hdiff", SearchOption.AllDirectories))
        {
            if (!TryDeleteFile(file, "hdiff artifact"))
            {
                success = false;
            }
        }

        string[] archivePatterns = [ "*.zip", "*.zip.*", "*.rar", "*.rar.*", "*.7z", "*.7z.*" ];

        foreach (string pattern in archivePatterns)
        {
            foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file);
                if (IsExcluded(info))
                {
                    continue;
                }

                if (!TryDeleteFile(file, "archive artifact"))
                {
                    success = false;
                }
            }
        }

        string[] rootTempPatterns = [ "*.tmp", "*.temp", "*.dmp", "*.bak", "*.aria2__temp" ];

        foreach (string pattern in rootTempPatterns)
        {
            foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file);
                if (IsExcluded(info))
                {
                    continue;
                }

                if (!TryDeleteFile(file, "temporary artifact"))
                {
                    success = false;
                }
            }
        }

        string[] targets =
        [
            "Logs", "Log", "SDKCaches", "webCaches", "blob_storage", "ldiff", "launcherDownload", "kr_game_cache",
            "Rp", "Links", ".quality", "quality", "CrashSightLog", "pipe_client", "TQM64", "wesight"
        ];

        foreach (string dir in Directory.GetDirectories(root))
        {
            if (!targets.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryDeleteDirectory(dir, "root cleanup directory"))
            {
                success = false;
            }
        }

        foreach (string dir in Directory.GetDirectories(gameFolder.FullName, "*", SearchOption.AllDirectories))
        {
            if (!targets.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryDeleteDirectory(dir, "game cleanup directory"))
            {
                success = false;
            }
        }

        foreach (string fileName in exactFiles)
        {
            string path = Path.Combine(root, fileName);
            if (File.Exists(path))
            {
                Logger.Error($"Cleanup incomplete: {path}");
                success = false;
            }
        }

        if (Directory.EnumerateFiles(root, "*.hdiff", SearchOption.AllDirectories).Any())
        {
            Logger.Error("Cleanup incomplete: one or more .hdiff files remain.");
            success = false;
        }

        foreach (string pattern in archivePatterns)
        {
            foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
            {
                if (!IsExcluded(new FileInfo(file)))
                {
                    Logger.Error($"Cleanup incomplete: archive artifact remains: {file}");
                    success = false;
                }
            }
        }

        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (targets.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
            {
                Logger.Error($"Cleanup incomplete: cleanup directory remains: {dir}");
                success = false;
            }
        }

        return success;
    }

    private static bool TryDeleteFile(string path, string reason)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            EnsureWritable(path);
            SetFileTitle("Cleaning", Path.GetFileName(path));
            File.Delete(path);

            if (File.Exists(path))
            {
                Logger.Error($"Failed to delete {reason}: {path}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to delete {reason} {path}: {ex.Message}");
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path, string reason)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            SetFileTitle("Deleting directory", Path.GetFileName(path));
            MakeWritableRecursive(path);
            Directory.Delete(path, true);

            if (Directory.Exists(path))
            {
                Logger.Error($"Failed to delete {reason}: {path}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to delete {reason} {path}: {ex.Message}");
            return false;
        }
    }
}
