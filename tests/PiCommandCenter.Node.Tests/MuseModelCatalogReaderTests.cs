using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Node.Runtime.Muse;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Model discovery against an in-memory MSP host: handshake, merge the partial live
/// <c>model/list</c> with curated discovery ids while preserving native evidence, terminate. No
/// session is started and no model quota is spent.
/// </summary>
public sealed class MuseModelCatalogReaderTests
{
    [Fact]
    public async Task Reads_all_curated_models_when_live_catalog_only_reports_muse_spark_1_3()
    {
        var host = new FakeMuseHost
        {
            Models =
            [
                new { modelId = "muse-spark-1.3", displayName = "Muse Spark 1.3" },
            ],
        };
        var factory = new FakeMuseProcessFactory(host);
        var reader = CreateReader(factory, options => options.Executable = "/opt/muse/bin/muse");

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(
            [
                "muse/muse-spark-1.2",
                "muse/muse-spark-1.2-contributor",
                "muse/muse-spark-1.3",
                "muse/muse-spark-1.3-contributor",
            ],
            result.Models.ToArray());
        Assert.Equal(["muse/muse-spark-1.3"], result.NativeModels);

        var start = Assert.Single(factory.Starts);
        Assert.Equal("/opt/muse/bin/muse", start.Executable);
        Assert.Equal(["serve", "--disable-write", "--disable-shell", "--no-session-log"], start.Arguments.ToArray());

        Assert.Equal(["initialize", "initialized", "model/list"], host.ReceivedMethods.ToArray());
        var list = host.Received[2].GetProperty("params");
        Assert.Equal(JsonValueKind.Object, list.ValueKind);
        Assert.Empty(list.EnumerateObject());
        Assert.True(host.Exited.IsCompleted);
        Assert.True(host.TerminateCalls >= 1);
    }

    [Fact]
    public void Parse_deduplicates_rows_excludes_invalid_defaults_and_preserves_future_models()
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            models = new object[]
            {
                new { modelId = "muse-spark-2.0" },
                new { modelId = "muse-spark-1.3" },
                new { modelId = "muse-spark-2.0" },
                new { modelId = " default " },
                new { modelId = "" },
                new { modelId = 42 },
                new { displayName = "missing id" },
                "malformed row",
            },
        });

        var result = MuseModelCatalogReader.Parse(response);

        Assert.Null(result.Error);
        Assert.Equal(
            [
                "muse/muse-spark-1.2",
                "muse/muse-spark-1.2-contributor",
                "muse/muse-spark-1.3",
                "muse/muse-spark-1.3-contributor",
                "muse/muse-spark-2.0",
            ],
            result.Models.ToArray());
        Assert.Equal(
            ["muse/muse-spark-1.3", "muse/muse-spark-2.0"],
            result.NativeModels);
    }

    [Fact]
    public void Empty_native_catalog_keeps_curated_discovery_models_without_native_evidence()
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            models = Array.Empty<object>(),
        });

        var result = MuseModelCatalogReader.Parse(response);

        Assert.Null(result.Error);
        Assert.Equal(
            [
                "muse/muse-spark-1.2",
                "muse/muse-spark-1.2-contributor",
                "muse/muse-spark-1.3",
                "muse/muse-spark-1.3-contributor",
            ],
            result.Models);
        Assert.Empty(result.NativeModels);
    }

    [Fact]
    public async Task Missing_model_list_is_a_stable_error()
    {
        var host = new FakeMuseHost();
        host.Handlers["model/list"] = static (h, id, _) => h.RespondAsync(id, new { catalog = Array.Empty<object>() });
        var reader = CreateReader(new FakeMuseProcessFactory(host));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Models);
        Assert.Empty(result.NativeModels);
        Assert.True(host.Exited.IsCompleted);
    }

    [Fact]
    public async Task Host_rejection_is_a_stable_error_without_raw_provider_output()
    {
        const string secret = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var host = new FakeMuseHost();
        host.Handlers["model/list"] = static (h, id, _) =>
            h.FailAsync(id, -32000, $"catalog backend refused token {secret}", "backendError");
        var reader = CreateReader(new FakeMuseProcessFactory(host));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Models);
        Assert.DoesNotContain(secret, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("muse login", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_failure_yields_local_login_guidance()
    {
        var host = new FakeMuseHost();
        host.Handlers["model/list"] = static (h, id, _) =>
            h.FailAsync(id, -32000, "Not signed in. Run `muse login` first.", "authRequired");
        var reader = CreateReader(new FakeMuseProcessFactory(host));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("muse login", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Unsupported_schema_version_is_a_stable_error_and_skips_model_list()
    {
        var host = new FakeMuseHost();
        host.Handlers["initialize"] = static (h, id, _) => h.RespondAsync(id, new { schema = new { version = 2 } });
        var reader = CreateReader(new FakeMuseProcessFactory(host));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Models);
        Assert.DoesNotContain("model/list", host.ReceivedMethods);
        Assert.True(host.Exited.IsCompleted);
    }

    [Fact]
    public async Task Unstartable_executable_is_a_stable_error_not_an_exception()
    {
        var reader = CreateReader(new FakeMuseProcessFactory());

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Silent_host_times_out_as_a_stable_error_and_is_terminated()
    {
        var host = new FakeMuseHost();
        host.Handlers["initialize"] = static (_, _, _) => Task.CompletedTask;
        var reader = CreateReader(new FakeMuseProcessFactory(host), options => options.StartTimeoutSeconds = 1);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Models);
        Assert.True(host.Exited.IsCompleted);
        Assert.True(host.TerminateCalls >= 1);
    }

    private static MuseModelCatalogReader CreateReader(
        FakeMuseProcessFactory factory,
        Action<MuseCodeOptions>? configure = null)
    {
        var options = new MuseCodeOptions
        {
            Executable = "muse-fake",
            StartTimeoutSeconds = 5,
            RequestTimeoutSeconds = 5,
            CancelGraceSeconds = 2,
        };
        configure?.Invoke(options);
        return new MuseModelCatalogReader(
            Options.Create(options),
            factory,
            NullLogger<MuseModelCatalogReader>.Instance);
    }
}
