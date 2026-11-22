extern alias SchemaWorker;

using SchemaWorker::Rolithax.Schema.HostTasks;
using SchemaWorker::Rolithax.Schema.Worker;

namespace Rolithax.Plugin.DaemonExtensions.Tests;

public sealed class SchemaWorkerTests
{
    [Theory]
    [InlineData("2.1.0", "2.1.0")]
    [InlineData("2.1.0-beta+1\r\n", "2.1.0-beta+1")]
    [InlineData("2.1.0\n", "2.1.0")]
    [InlineData("2.1.0\n\n", null)]
    [InlineData("version 2.1.0", null)]
    [InlineData("2.1.0\u001B", null)]
    public void ProbeVersionRequiresOneExactStdoutValue(string output, string? expected) =>
        Assert.Equal(expected, WorkerProtocol.ParseProbeVersion(output));

    [Fact]
    public async Task VersionProbeRequiresMatchingConfirmationAndIsNotListedAsGenericTask()
    {
        var preset = new Preset("version", "Check version", "A read-only check", "version.exe", [], ["allowed"], [7],
            VersionProbe: new("mod", "example"));
        var config = new WorkerConfig("", "", "", [preset]);
        var describe = new WorkerRequest("describeVersionProbe", "allowed", 7, Kind: "mod", Target: "example");
        var descriptor = await Program.DispatchAsync(config, describe, TestContext.Current.CancellationToken);
        Assert.Equal(200, descriptor.Code);
        Assert.NotNull(descriptor.CommandProbe);
        Assert.True(WorkerProtocol.IsProbeConfirmation(descriptor.CommandProbe.Id));
        Assert.Equal("Check version", descriptor.CommandProbe.Title);
        Assert.Empty((await Program.DispatchAsync(config, new("list", "allowed", 7), TestContext.Current.CancellationToken)).Tasks!);
        var confirm = describe with { Op = "runVersionProbe", TaskId = descriptor.CommandProbe.Id };
        foreach (var request in new[]
        {
            describe with { TaskId = descriptor.CommandProbe.Id },
            confirm with { TaskId = "version" },
            confirm with { UserId = "other" },
            confirm with { InstanceId = 8 },
            confirm with { Kind = "mcdrPlugin" },
            confirm with { Target = "other" },
            new WorkerRequest("run", "allowed", 7, "version"),
        })
            Assert.Equal(403, (await Program.DispatchAsync(config, request, TestContext.Current.CancellationToken)).Code);
    }
}
