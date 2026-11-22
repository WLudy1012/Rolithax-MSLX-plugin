using System.IO.Compression;
using System.Security.Cryptography;
using MSLX.SDK.Models;
using Rolithax.Plugin.DaemonExtensions.Schema;
using Rolithax.Schema.HostTasks;

namespace Rolithax.Plugin.DaemonExtensions.Tests;

public sealed class SchemaTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("rolithax-schema-test-");

    [Fact]
    public void McdrLayoutSeparatesPluginDirectoriesAndReadsInstalledVersions()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "config.yml"),
            "working_directory: 'game server'\nplugin_directories:\n- plugins\n- \"extra plugins\"\n");
        var instance = Server("python -m mcdreforged start");
        var layout = InstanceMetadata.Layout(instance);
        Assert.Equal("mcdr", layout.Kind);
        Assert.Equal("game server/server.properties", layout.ServerPropertiesPath);
        Assert.Equal("game server/plugins", layout.MinecraftPluginsDirectory);
        Assert.Equal(["plugins", "extra plugins"], layout.PluginDirectories);
        Archive("extra plugins/package.mcdr", "mcdreforged.plugin.json", """{"id":"testing","version":"2.1.0"}""");
        Archive("game server/plugins/paper.jar", "paper-plugin.yml", "name: Example\nversion: '3.0.0'\n");
        Archive("game server/mods/fabric.jar", "fabric.mod.json", """{"id":"clienttest","version":"1.2.3"}""");
        Archive("game server/mods/forge.jar", "META-INF/mods.toml", "[[mods]]\nmodId=\"forge_example\"\nversion=\"${file.jarVersion}\"\n",
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nImplementation-Version: 1.1.0\r\n\r\n"));

        var token = TestContext.Current.CancellationToken;
        Assert.Equal(new("found", "2.1.0", "mcdreforged.plugin.json"), InstanceMetadata.Probe(instance, "mcdrPlugin", "testing", token));
        Assert.Equal("3.0.0", InstanceMetadata.Probe(instance, "minecraftPlugin", "example", token).Version);
        Assert.Equal("1.2.3", InstanceMetadata.Probe(instance, "mod", "clienttest", token).Version);
        Assert.Equal("1.1.0", InstanceMetadata.Probe(instance, "mod", "forge_example", token).Version);
        Assert.Equal("missing", InstanceMetadata.Probe(instance, "mcdrPlugin", "different", token).Status);
        File.WriteAllText(Path.Combine(_root.FullName, "plugins", "unknown.py"), "");
        Assert.Equal("unknown", InstanceMetadata.Probe(instance, "mcdrPlugin", "different", token).Status);
    }

    [Fact]
    public void ProbeRejectsUnsafePathsAndKeepsAmbiguousVersionsUnknown()
    {
        var instance = Server("python -m mcdreforged start");
        File.WriteAllText(Path.Combine(_root.FullName, "config.yml"), "working_directory: '../outside'\n");
        Assert.Throws<InvalidDataException>(() => InstanceMetadata.Layout(instance));
        File.WriteAllText(Path.Combine(_root.FullName, "config.yml"), "working_directory: server\nplugin_directories: [plugins]\n");
        Archive("plugins/a.mcdr", "mcdreforged.plugin.json", """{"id":"testing","version":"1.0.0"}""");
        Archive("plugins/b.mcdr", "mcdreforged.plugin.json", """{"id":"testing","version":"2.0.0"}""");
        var token = TestContext.Current.CancellationToken;
        Assert.Equal("unknown", InstanceMetadata.Probe(instance, "mcdrPlugin", "testing", token).Status);
        Assert.Throws<ArgumentException>(() => InstanceMetadata.Probe(instance, "mcdrPlugin", "a/b", token));
    }

    [Fact]
    public async Task WorkerRequestsAuthenticateBothDirectionsAndBindHandshake()
    {
        var token = TestContext.Current.CancellationToken;
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(32);
        using var stream = new MemoryStream();
        var expected = new WorkerRequest("run", "user-1", 3, "version");
        await WorkerProtocol.WriteSignedAsync(stream, key, nonce, false, expected, token);
        stream.Position = 0;
        Assert.Equal(expected, await WorkerProtocol.ReadSignedAsync<WorkerRequest>(stream, key, nonce, false, token));
        stream.Position = 0;
        await Assert.ThrowsAsync<CryptographicException>(() => WorkerProtocol.ReadSignedAsync<WorkerRequest>(stream, key, nonce, true, token));
        stream.Position = 0;
        await Assert.ThrowsAsync<CryptographicException>(() =>
            WorkerProtocol.ReadSignedAsync<WorkerRequest>(stream, key, RandomNumberGenerator.GetBytes(32), false, token));
    }

    private McServerInfo.ServerInfo Server(string args)
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, "plugins"));
        return new() { ID = 1, Base = _root.FullName, Core = "paper.jar", Java = "java", Name = "Example", Args = args };
    }

    private void Archive(string path, string metadata, string content, params (string Name, string Content)[] extra)
    {
        var full = Path.Combine(_root.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var archive = ZipFile.Open(full, ZipArchiveMode.Create);
        foreach (var item in extra.Prepend((Name: metadata, Content: content)))
        {
            using var writer = new StreamWriter(archive.CreateEntry(item.Name).Open());
            writer.Write(item.Content);
        }
    }

    public void Dispose() => _root.Delete(recursive: true);
}
