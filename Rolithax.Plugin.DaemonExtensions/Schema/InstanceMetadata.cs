using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using MSLX.SDK.Models;
using Rolithax.Schema.HostTasks;

namespace Rolithax.Plugin.DaemonExtensions.Schema;

internal sealed record InstanceLayout(
    string Kind, string GameDirectory, string[] PluginDirectories, string MinecraftPluginsDirectory,
    string ModsDirectory, string ServerPropertiesPath);

internal sealed record VersionProbe(string Status, string? Version = null, string? Source = null, TaskSummary? CommandProbe = null);

internal static partial class InstanceMetadata
{
    private const int TextLimit = 64 * 1024;
    private const long ArchiveLimit = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly VersionProbe Unknown = new("unknown");
    private static readonly VersionProbe Missing = new("missing");
    private sealed record MetadataFile(string Name, string Text, string? JarVersion = null);

    public static InstanceLayout Layout(McServerInfo.ServerInfo instance)
    {
        var root = InstanceRoot(instance);
        var properties = Relative(instance.ServerPropertiesPath, "server.properties");
        var pluginDirectory = Relative(instance.PluginsPath, "plugins");
        var modDirectory = Relative(instance.ModsPath, "mods");
        var gameDirectory = Parent(properties);
        if (instance.Args?.Contains("mcdreforged", StringComparison.OrdinalIgnoreCase) != true)
            return new("minecraft", gameDirectory, [], pluginDirectory, modDirectory, properties);

        string? mcdrConfig;
        try { mcdrConfig = ReadText(root, "config.yml"); }
        catch (FileNotFoundException) { mcdrConfig = null; }
        catch (DirectoryNotFoundException) { mcdrConfig = null; }
        var directories = new[] { "plugins" };
        if (mcdrConfig is not null)
        {
            gameDirectory = Relative(Scalar(mcdrConfig, "working_directory") ?? gameDirectory, "server", allowCurrent: true);
            directories = PluginDirectories(mcdrConfig).Select(path => Relative(path, "plugins", allowCurrent: true))
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        else if (gameDirectory.Length == 0)
        {
            gameDirectory = "server";
        }
        if (PathEquals(properties, "server.properties") || PathEquals(properties, "server/server.properties"))
            properties = Join(gameDirectory, "server.properties");
        if (PathEquals(pluginDirectory, "plugins") || PathEquals(pluginDirectory, "server/plugins"))
            pluginDirectory = Join(gameDirectory, "plugins");
        if (PathEquals(modDirectory, "mods") || PathEquals(modDirectory, "server/mods"))
            modDirectory = Join(gameDirectory, "mods");
        return new("mcdr", gameDirectory, directories, pluginDirectory, modDirectory, properties);
    }

    public static VersionProbe Probe(McServerInfo.ServerInfo instance, string kind, string target, CancellationToken token)
    {
        if (!WorkerProtocol.IsProbeTarget(target)) throw new ArgumentException("插件或模组标识无效");
        var root = InstanceRoot(instance);
        var layout = Layout(instance);
        if (kind == "mcdrPlugin" && layout.Kind != "mcdr") return Unknown;
        var directories = kind switch
        {
            "mcdrPlugin" => layout.PluginDirectories,
            "minecraftPlugin" => [layout.MinecraftPluginsDirectory],
            "mod" => [layout.ModsDirectory],
            _ => throw new ArgumentException("不支持此版本探测类型"),
        };
        var clock = Stopwatch.StartNew();
        var checkedFiles = 0;
        var incomplete = false;
        var versions = new HashSet<string>(StringComparer.Ordinal);
        string? source = null;
        foreach (var directory in directories)
        {
            var full = CheckedPath(root, directory, allowMissing: true);
            if (!Directory.Exists(full)) continue;
            foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (++checkedFiles > 256 || clock.Elapsed > TimeSpan.FromSeconds(2)) return Unknown;
                var path = Join(directory, entry.Name);
                var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (kind == "mcdrPlugin" && extension == ".py")
                {
                    incomplete = true;
                    continue;
                }
                var isFolder = entry is DirectoryInfo;
                if (isFolder && kind != "mcdrPlugin" || !isFolder &&
                    (kind == "mcdrPlugin" ? extension is not (".mcdr" or ".pyz") : extension != ".jar")) continue;
                try
                {
                    var metadata = isFolder
                        ? new[] { new MetadataFile("mcdreforged.plugin.json", ReadText(root, Join(path, "mcdreforged.plugin.json"))) }
                        : ReadArchive(root, path, kind);
                    if (metadata.Length == 0) incomplete = true;
                    foreach (var item in metadata)
                    {
                        var entries = ReadMetadata(item);
                        if (entries.Length == 0) incomplete = true;
                        foreach (var value in entries)
                        {
                            if (value.Id is null)
                            {
                                incomplete = true;
                                continue;
                            }
                            if (!string.Equals(value.Id, target, StringComparison.OrdinalIgnoreCase)) continue;
                            if (value.Version is null) return Unknown;
                            if (versions.Count == 0) source = item.Name;
                            versions.Add(value.Version);
                            if (versions.Count > 1) return Unknown;
                        }
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    incomplete = true;
                }
            }
        }
        return versions.Count == 1 ? new("found", versions.Single(), source) : incomplete ? Unknown : Missing;
    }

    private static MetadataFile[] ReadArchive(string root, string path, string kind)
    {
        using var file = OpenRead(root, path);
        if (file.Length > ArchiveLimit) throw new InvalidDataException();
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > 24_000) throw new InvalidDataException();
        string[] names = kind switch
        {
            "mcdrPlugin" => ["mcdreforged.plugin.json"],
            "minecraftPlugin" => ["paper-plugin.yml", "plugin.yml"],
            _ => ["fabric.mod.json", "quilt.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml"],
        };
        var metadata = new List<MetadataFile>();
        foreach (var name in names)
        {
            var text = ArchiveText(zip, name);
            if (text is null) continue;
            var jarVersion = name.EndsWith(".toml", StringComparison.Ordinal) && text.Contains("${file.jarVersion}", StringComparison.Ordinal)
                ? JarVersion(ArchiveText(zip, "META-INF/MANIFEST.MF"))
                : null;
            metadata.Add(new(name, text, jarVersion));
        }
        return metadata.ToArray();
    }

    private static string? ArchiveText(ZipArchive zip, string name)
    {
        var matches = zip.Entries.Where(entry => entry.FullName == name).Take(2).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || matches[0].Length > TextLimit) throw new InvalidDataException();
        using var content = matches[0].Open();
        return ReadText(content);
    }

    private static string? JarVersion(string? manifest)
    {
        if (manifest is null) return null;
        var lines = manifest.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n ", "", StringComparison.Ordinal).Split('\n');
        var versions = lines.TakeWhile(line => line.Length > 0)
            .Where(line => line.StartsWith("Implementation-Version: ", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return versions.Length == 1 ? SafeVersion(versions[0]["Implementation-Version: ".Length..]) : null;
    }

    private static (string? Id, string? Version)[] ReadMetadata(MetadataFile file)
    {
        var (name, text, jarVersion) = file;
        if (name.EndsWith(".json", StringComparison.Ordinal))
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (name == "quilt.mod.json" && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("quilt_loader", out var loader))
                root = loader;
            return [(JsonString(root, "id"), SafeVersion(JsonString(root, "version")))];
        }
        if (name.EndsWith(".toml", StringComparison.Ordinal))
        {
            var entries = new List<(string?, string?)>();
            string? id = null, version = null;
            var inMod = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    if (inMod) entries.Add((id, SafeVersion(version)));
                    inMod = line == "[[mods]]";
                    id = version = null;
                    continue;
                }
                if (!inMod) continue;
                var split = line.IndexOf('=');
                if (split < 0) continue;
                var key = line[..split].Trim();
                if (key == "modId") id = PlainValue(line[(split + 1)..]);
                if (key == "version")
                {
                    version = PlainValue(line[(split + 1)..]);
                    if (version == "${file.jarVersion}") version = jarVersion;
                }
            }
            if (inMod) entries.Add((id, SafeVersion(version)));
            return entries.ToArray();
        }
        return [(Scalar(text, "name"), SafeVersion(Scalar(text, "version")))];
    }

    private static string[] PluginDirectories(string config)
    {
        var lines = config.Split('\n');
        var entries = Array.FindAll(lines, line => line.StartsWith("plugin_directories:", StringComparison.Ordinal));
        if (entries.Length == 0) return ["plugins"];
        if (entries.Length != 1) throw new InvalidDataException();
        var value = entries[0].Split(':', 2)[1].Trim();
        if (value.StartsWith('['))
        {
            var close = value.LastIndexOf(']');
            if (close <= 0 || !PlainValue(value[(close + 1)..]).Equals(string.Empty, StringComparison.Ordinal))
                throw new InvalidDataException();
            var inner = value[1..close].Trim();
            if (inner.Length == 0) return [];
            // Quoted commas are not interpreted by this limited layout reader.
            return DirectoryValues(inner.Split(',').Select(PlainValue));
        }
        if (PlainValue(value).Length > 0) throw new InvalidDataException();
        return DirectoryValues(lines.Skip(Array.IndexOf(lines, entries[0]) + 1)
            .Select(line => line.Trim()).TakeWhile(line => line.Length == 0 || line.StartsWith('#') || line.StartsWith("- ", StringComparison.Ordinal))
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => PlainValue(line[2..])));
    }

    private static string[] DirectoryValues(IEnumerable<string> values)
    {
        var result = values.Take(9).ToArray();
        if (result.Length > 8 || result.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException();
        return result;
    }

    private static string? Scalar(string input, string key)
    {
        var matches = input.Split('\n').Where(line => line.StartsWith(key + ":", StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidDataException();
        return matches.Length == 0 ? null : PlainValue(matches[0][(key.Length + 1)..]);
    }

    private static string PlainValue(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0 || value.StartsWith('#')) return "";
        if (value[0] is '\'' or '"')
        {
            var end = value.IndexOf(value[0], 1);
            if (end < 0 || value[(end + 1)..].TrimStart() is { Length: > 0 } tail && !tail.StartsWith('#'))
                throw new InvalidDataException();
            return value[1..end];
        }
        if (value[0] is '&' or '*' or '!' or '|' or '>' or '[' or '{') throw new InvalidDataException();
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment < 0 ? value : value[..comment].TrimEnd();
    }

    private static string? JsonString(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? SafeVersion(string? version) => version is not null && Version().IsMatch(version) ? version : null;
    private static string Parent(string value) => value.LastIndexOf('/') is var index && index >= 0 ? value[..index] : "";
    private static string Join(string directory, string leaf) => directory.Length == 0 ? leaf : directory + "/" + leaf;
    private static string InstanceRoot(McServerInfo.ServerInfo instance) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.Base));
    private static bool PathEquals(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string Relative(string? value, string fallback, bool allowCurrent = false)
    {
        var path = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace('\\', '/');
        if (allowCurrent && path == ".") return "";
        if (path.Length == 0 && allowCurrent) return "";
        if (path.Length > 512 || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.EndsWith('.') || segment.EndsWith(' ') || segment.Any(character => char.IsControl(character) || ":*?\"<>|".Contains(character))))
            throw new InvalidDataException("路径必须位于当前实例内");
        return path;
    }

    private static string CheckedPath(string root, string path, bool allowMissing = false)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        var current = root;
        if (path.Length == 0) return current;
        foreach (var part in Relative(path, "").Split('/'))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
        return current;
    }

    private static FileStream OpenRead(string root, string path)
    {
        var full = CheckedPath(root, path);
        var handle = OperatingSystem.IsLinux()
            ? new SafeFileHandle(LinuxOpen(full, 0xA0800), ownsHandle: true)
            : File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (handle.IsInvalid) throw new IOException("无法读取实例元数据");
            if (!PathEquals(ActualPath(handle), full)) throw new InvalidDataException("文件路径在读取前发生变更");
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string ActualPath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
            return new FileInfo("/proc/self/fd/" + handle.DangerousGetHandle().ToInt64()).LinkTarget ?? "";
        if (OperatingSystem.IsWindows())
        {
            var buffer = new StringBuilder(32768);
            var length = WindowsFinalPath(handle, buffer, buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return "";
            var path = buffer.ToString();
            return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
                : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
        throw new PlatformNotSupportedException("当前系统未启用实例元数据读取");
    }

    private static string ReadText(string root, string relative)
    {
        using var file = OpenRead(root, relative);
        if (file.Length > TextLimit) throw new InvalidDataException();
        return ReadText(file);
    }

    private static string ReadText(Stream stream)
    {
        var bytes = new byte[TextLimit + 1];
        var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (length > TextLimit) throw new InvalidDataException();
        return Utf8.GetString(bytes, 0, length).TrimStart('\uFEFF');
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WindowsFinalPath(SafeFileHandle handle, StringBuilder path, int length, uint flags);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.+\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Version();
}
