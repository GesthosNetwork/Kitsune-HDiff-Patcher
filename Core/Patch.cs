using System.Diagnostics;
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
        "YuanShen_Data",
        "StarRail_Data",
        "ZenlessZoneZero_Data",
        "ZenlessZoneZeroBeta_Data",
        "BH3_Data",
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
        EXCLUDE_FILES
            .Select(NormalizeText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

    private static string? GAME_VERSION = null;
    private static bool pending_delete_for_migration = false;

    private static readonly HashSet<string> deleteList = new(StringComparer.OrdinalIgnoreCase);

    private static string? HPATCHZ_PATH;
    private static string? SEVEN_ZIP_PATH;

    public static int Run(string[] args)
    {
        try
        {
            deleteList.Clear();
            GAME_VERSION = null;
            pending_delete_for_migration = false;

            Program.SetTitle("Detecting game folder...");
            var gameFolder = DetectGameFolder();

            Program.SetTitle("Preparing tools...");
            CheckTools();

            bool patch_done = false;

            Program.SetTitle("Processing multipart archives...");
            if (ExtractAllMultipartAndProcess(gameFolder))
            {
                patch_done = true;
            }

            Program.SetTitle("Processing archives...");
            var cwd = Directory.GetCurrentDirectory();
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

            foreach (var archive_name in filtered)
            {
                var archive_path = new FileInfo(archive_name);
                ExtractSingleArchive(archive_path);

                if (ProcessLogicalArchive(archive_name, gameFolder))
                {
                    patch_done = true;
                }
            }

            if (patch_done)
            {
                Program.SetTitle("Detecting game version...");
                GAME_VERSION = DetectGameVersionAfterPatch(gameFolder);

                if (pending_delete_for_migration)
                {
                    Program.SetTitle("Deleting obsolete files...");
                    DeleteFiles();
                }

                if (GAME_VERSION != null)
                {
                    Program.SetTitle("Writing configuration...");
                    WriteConfigIni();
                }

                Program.SetTitle("Cleaning temporary files...");
                CleanupAuxFiles(gameFolder);
            }

            Program.SetTitle("Removing empty directories...");
            CleanupEmptyDirs(gameFolder);
            CleanupEmptyDirsRoot();

            Program.SetTitle("Verifying files...");
            Logger.Info("Patching finished.");
            return 0;
        }
        catch (Exception ex)
        {
            Program.SetTitle("Failed");
            Logger.Error(ex.Message);
            return 1;
        }
    }

    private static string NormalizeText(string value)
    {
        return value
            .Replace('\\', '/')
            .ToLowerInvariant()
            .TrimStart('.', '/');
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

    private static int RunProcess(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
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

        process.WaitForExit();

        return process.ExitCode;
    }

    private static void ReplaceTextInFile(string filepath)
    {
        if (!File.Exists(filepath))
        {
            return;
        }

        string text = File.ReadAllText(filepath, Encoding.UTF8);
        text = text.Replace("{\"remoteName\": \"", "")
                   .Replace("\"}", "")
                   .Replace("/", "\\");
        File.WriteAllText(filepath, text, new UTF8Encoding(false));
    }

    private static DirectoryInfo DetectGameFolder()
    {
        foreach (var folder in GAME_FOLDERS)
        {
            var dir = new DirectoryInfo(folder);
            if (dir.Exists)
            {
                Logger.Info($"Detected game folder: {folder}");
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

        foreach (var line in File.ReadAllLines(delete_txt, Encoding.UTF8))
        {
            string raw = line.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

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
                continue;
            }

            MakeWritableRecursive(target);

            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                    Logger.Info($"Deleted file: {target}");
                }
                else
                {
                    Directory.Delete(target, true);
                    Logger.Info($"Deleted directory tree: {target}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to delete {target}: {ex.Message}");
            }
        }

        try
        {
            File.Delete(delete_txt);
        }
        catch {}
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

    private static bool ApplyHDiff()
    {
        bool patched = false;

        string hdifffiles_txt = "hdifffiles.txt";
        if (File.Exists(hdifffiles_txt))
        {
            ReplaceTextInFile(hdifffiles_txt);

            foreach (var line in File.ReadAllLines(hdifffiles_txt, Encoding.UTF8))
            {
                string target = line.Trim();
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                string original_file = target;
                string hdiff = $"{target}.hdiff";

                if (!File.Exists(original_file))
                {
                    Logger.Warning($"Target file not found: {original_file}");
                    continue;
                }

                if (!File.Exists(hdiff))
                {
                    Logger.Warning($"Patch file not found: {hdiff}");
                    continue;
                }

                EnsureWritable(original_file);

                try
                {
                    Program.SetTitle($"Patching {ShortTitleText(Path.GetFileName(original_file))}");

                    int code = RunProcess(
                        HPATCHZ_PATH ?? throw new InvalidOperationException("hpatchz.exe not loaded."),
                        new[]
                        {
                            "-f",
                            Path.GetFullPath(original_file),
                            Path.GetFullPath(hdiff),
                            Path.GetFullPath(original_file)
                        });

                    if (code != 0)
                    {
                        throw new Exception($"hpatchz exit code {code}");
                    }

                    patched = true;
                    Logger.Info($"Patched (legacy): {original_file}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"hpatchz failed for {original_file}: {ex.Message}");
                    throw;
                }

                try
                {
                    File.Delete(hdiff);
                }
                catch {}
            }

            try
            {
                File.Delete(hdifffiles_txt);
            }
            catch {}
        }

        foreach (var item in ReadHdiffmapJson())
        {
            var source = item.Source;
            var patch = item.Patch;
            var target = item.Target;

            if (!source.Exists)
            {
                Logger.Warning($"Source file not found: {source.FullName}");
                continue;
            }

            if (!patch.Exists)
            {
                Logger.Warning($"Patch file not found: {patch.FullName}");
                continue;
            }

            if (target.Directory != null)
            {
                target.Directory.Create();
            }

            EnsureWritable(source.FullName);

            try
            {
                Program.SetTitle($"Patching {ShortTitleText(target.Name)}");

                int code = RunProcess(
                    HPATCHZ_PATH ?? throw new InvalidOperationException("hpatchz.exe not loaded."),
                    new[]
                    {
                        "-f", source.FullName, patch.FullName, target.FullName
                    });

                if (code != 0)
                {
                    throw new Exception($"hpatchz exit code {code}");
                }

                patched = true;
                Logger.Info($"Patched (map): {source.FullName} -> {target.FullName}");

                if (!source.FullName.Equals(target.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        EnsureWritable(source.FullName);
                        File.Delete(source.FullName);
                        Logger.Info($"Deleted old source file: {source.FullName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to delete old source file {source.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"hpatchz failed for {source.FullName}: {ex.Message}");
                throw;
            }

            try
            {
                File.Delete(patch.FullName);
            }
            catch {}
        }

        try
        {
            if (File.Exists("hdiffmap.json"))
            {
                File.Delete("hdiffmap.json");
            }
        }
        catch {}

        return patched;
    }

    private static void ExtractWith7z(FileInfo archive)
    {
        int code = RunProcess(
            SEVEN_ZIP_PATH ?? throw new InvalidOperationException("7z.exe not loaded."),
            new[]
            {
                "x", archive.FullName, "-o.", "-y"
            });

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
            Logger.Info($"Processing multipart archive: {first.Name}");
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

        if (MULTIPART_FIRST_001_REGEX.IsMatch(ln))
        {
            return true;
        }

        if (MULTIPART_PART1_RAR_REGEX.IsMatch(ln))
        {
            return true;
        }

        if (MULTIPART_ANY_PART_REGEX.IsMatch(ln))
        {
            return true;
        }

        if (MULTIPART_ANY_RAR_PART_REGEX.IsMatch(ln))
        {
            return true;
        }

        return false;
    }

    private static void ExtractSingleArchive(FileInfo archive)
    {
        try
        {
            Program.SetTitle($"Extracting {ShortTitleText(archive.Name)}");
            Logger.Info($"Processing archive: {archive.Name}");
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
        if (versionTo == null)
        {
            return false;
        }

        Program.SetTitle("Migrating audio files...");

        try
        {
            var parts = versionTo.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .ToArray();

            if (parts.Length < 2)
            {
                return false;
            }

            int major = parts[0];
            int minor = parts[1];

            if (IsVersionLess(major, minor, 3, 6))
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

        foreach (var file in Directory.GetFiles(oldPath, "*", SearchOption.AllDirectories))
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
            }
            catch {}
        }

        try
        {
            Directory.Delete(oldPath, true);
        }
        catch {}

        Logger.Success("Audio migration completed.");
        return true;
    }

    private static bool ProcessLogicalArchive(string archiveName, DirectoryInfo gameFolder)
    {
        bool patched = false;
        var versions = ParseFromToVersionsFromName(archiveName);

        bool needsMigration = false;

        if (versions.From != null && versions.To != null)
        {
            try
            {
                var from = versions.From.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(2).Select(int.Parse).ToArray();

                var to = versions.To.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(2).Select(int.Parse).ToArray();

                bool oldVersion = IsVersionLess(from[0], from[1], 3, 6);
                bool newVersion = IsVersionAtLeast(to[0], to[1], 3, 6);

                if (oldVersion && newVersion)
                {
                    needsMigration = true;
                }
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
        if (ApplyHDiff())
        {
            patched = true;
        }

        return patched;
    }

    private static bool ExtractAllMultipartAndProcess(DirectoryInfo gameFolder)
    {
        bool patchedAny = false;

        foreach (var first in GetMultipartFirstParts())
        {
            try
            {
                if (ExtractMultipartAndProcess(first, gameFolder))
                {
                    patchedAny = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error processing multipart {first.Name}: {ex.Message}");
            }
        }

        return patchedAny;
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
                        Logger.Info($"Deleted empty directory (root): {dir}");
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

    private static void WriteConfigIni()
    {
        if (GAME_VERSION == null)
        {
            return;
        }

        string[] content =
        [
            "[General]",
            "channel=1",
            "cps=hoyoverse",
            $"game_version={GAME_VERSION}",
            "sub_channel=0"
        ];

        File.WriteAllLines("config.ini", content, new UTF8Encoding(false));
    }

    private static void CleanupAuxFiles(DirectoryInfo gameFolder)
    {
        Program.SetTitle("Cleaning temporary files...");

        string[] patterns =
        [
            "*.py", "*.bat", "*.zip", "*.zip.*", "*.zip.001", "*.zip.002", "*.rar", "*.rar.*",
            "*.rar.001", "*.rar.002", "*.part1.rar", "*.part2.rar", "*.part*.rar", "*.7z", "*.7z.*",
            "*.7z.001", "*.7z.002", "hpatchz.exe", "hdiffz.exe", "7z.exe", "version.dll", "*.temp",
            "*.tmp", "*.dmp", "*.bak", "*.txt", "*.log", "*.md"
        ];

        string root = Directory.GetCurrentDirectory();

        foreach (var pattern in patterns)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);

                        SetFileTitle("Cleaning", info.Name);

                        string normalized = NormalizePathText(info.FullName);

                        if (deleteList.Contains(normalized))
                        {
                            MakeWritableRecursive(info.FullName);
                            info.Delete();
                            Logger.Info($"Deleted from deletefiles.txt: {info.FullName}");
                            continue;
                        }

                        if (IsExcluded(info))
                        {
                            continue;
                        }

                        info.Delete();
                    }
                    catch {}
                }
            }
            catch {}
        }

        string[] targets =
        [
            "Logs", "Log", "SDKCaches", "webCaches", "blob_storage", "ldiff", "launcherDownload", "kr_game_cache",
            "Rp", ".quality", "quality", "CrashSightLog", "pipe_client", "TQM64", "wesight"
        ];

        foreach (var dir in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(dir);
            if (!targets.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                SetFileTitle("Deleting directory", name);
                MakeWritableRecursive(dir);
                Directory.Delete(dir, true);
                Logger.Info($"Deleted directory tree (root): {dir}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to delete {dir}: {ex.Message}");
            }
        }

        foreach (var dir in Directory.GetDirectories(gameFolder.FullName, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(dir);
            if (!targets.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                SetFileTitle("Deleting directory", name);
                MakeWritableRecursive(dir);
                Directory.Delete(dir, true);
                Logger.Info($"Deleted directory tree (game folder): {dir}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to delete {dir}: {ex.Message}");
            }
        }
    }

    private static string? DetectGameVersionAfterPatch(DirectoryInfo gameFolder)
    {
        string settings_json = Path.Combine(gameFolder.FullName, "StreamingAssets", "asb_settings.json");
        if (File.Exists(settings_json))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings_json, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("variance", out var variance))
                {
                    var result = VERSION_REGEX.Match(variance.GetString() ?? "");
                    if (result.Success)
                    {
                        return NormalizeVersion(result.Groups[1].Value);
                    }
                }
            }
            catch {}
        }

        string bin_ver = Path.Combine(gameFolder.FullName, "StreamingAssets", "BinaryVersion.bytes");
        if (File.Exists(bin_ver))
        {
            try
            {
                string data = File.ReadAllText(bin_ver, Encoding.UTF8);
                var match = VERSION_REGEX.Match(data);
                if (match.Success)
                {
                    return NormalizeVersion(match.Groups[1].Value);
                }
            }
            catch
            {
                try
                {
                    string data = Encoding.UTF8.GetString(File.ReadAllBytes(bin_ver));
                    var match = VERSION_REGEX.Match(data);
                    if (match.Success)
                    {
                        return NormalizeVersion(match.Groups[1].Value);
                    }
                }
                catch {}
            }
        }

        string version_info = "version_info";
        if (File.Exists(version_info))
        {
            try
            {
                string data = File.ReadAllText(version_info, Encoding.UTF8);
                var match = VERSION_REGEX.Match(data);
                if (match.Success)
                {
                    return NormalizeVersion(match.Groups[1].Value);
                }
            }
            catch
            {
                try
                {
                    string data = File.ReadAllText(version_info);
                    var match = VERSION_REGEX.Match(data);
                    if (match.Success)
                    {
                        return NormalizeVersion(match.Groups[1].Value);
                    }
                }
                catch {}
            }
        }

        Logger.Warning("Game version could not be detected after patch.");
        return null;
    }
}
