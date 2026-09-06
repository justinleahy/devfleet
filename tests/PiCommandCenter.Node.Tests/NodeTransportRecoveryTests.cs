using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Node.Projects;
using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.SubscriptionUsage;
using PiCommandCenter.Node.Verification;

namespace PiCommandCenter.Node.Tests;

public sealed class NodeTransportRecoveryTests
{
    [Fact]
    public async Task OnRecoverAssignmentAsync_delivers_command_to_subscriber()
    {
        var client = CreateClient();
        RecoverAssignmentCommandMessage? received = null;
        client.RecoverAssignmentReceived += command =>
        {
            received = command;
            return Task.CompletedTask;
        };

        var command = Command();
        await client.OnRecoverAssignmentAsync(command);

        Assert.Same(command, received);
    }

    [Fact]
    public async Task OnRecoverAssignmentAsync_does_not_throw_when_unsubscribed()
    {
        var client = CreateClient();

        await client.OnRecoverAssignmentAsync(Command());
    }

    [Fact]
    public async Task OnRecoverAssignmentAsync_swallows_subscriber_exceptions()
    {
        var client = CreateClient();
        client.RecoverAssignmentReceived += _ => throw new InvalidOperationException("handler failed");

        await client.OnRecoverAssignmentAsync(Command());
    }

    [Fact]
    public async Task ReportRecoveryProgressAsync_requires_started_connection()
    {
        var client = CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ReportRecoveryProgressAsync(Progress(), CancellationToken.None));

        Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportRecoveryProofAsync_requires_started_connection()
    {
        var client = CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ReportRecoveryProofAsync(Proof(), CancellationToken.None));

        Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportRecoveryProgressAsync_rejects_null_payload()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.ReportRecoveryProgressAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ReportRecoveryProofAsync_rejects_null_payload()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.ReportRecoveryProofAsync(null!, CancellationToken.None));
    }


    private static RecoverAssignmentCommandMessage Command() =>
        new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            1,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            "claim-token",
            7,
            DateTimeOffset.Parse("2026-09-06T12:00:00Z"));

    private static AssignmentRecoveryProgressMessage Progress()
    {
        var zero = new RecoveryKnownCountMessage(0, null);
        return new AssignmentRecoveryProgressMessage(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            1,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            "claim-token",
            7,
            DateTimeOffset.Parse("2026-09-06T12:00:01Z"),
            "stopping",
            zero,
            zero,
            zero,
            zero,
            zero,
            []);
    }

    private static AssignmentRecoveryProofMessage Proof()
    {
        var zero = new RecoveryKnownCountMessage(0, null);
        return new AssignmentRecoveryProofMessage(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            1,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            "claim-token",
            7,
            DateTimeOffset.Parse("2026-09-06T12:00:02Z"),
            true,
            zero,
            zero,
            zero,
            zero,
            zero,
            0,
            null,
            [],
            [],
            new RecoveryRepositoryStatusMessage(
                true,
                "abc",
                "main",
                "clean",
                "clean",
                zero,
                [],
                DateTimeOffset.Parse("2026-09-06T12:00:02Z")));
    }

    private static NodeTransportClient CreateClient()
    {
        var missingCredentialPath = Path.Combine(
            Path.GetTempPath(),
            "devfleet-missing-node-credential",
            Guid.NewGuid().ToString("N"),
            "node.token");
        var credentials = new NodeCredentialLoader(Options.Create(new NodeAuthenticationOptions
        {
            CredentialFile = missingCredentialPath,
        }));

        return new NodeTransportClient(
            Options.Create(new NodeOptions { ControlPlaneUrl = "https://control.example.com" }),
            credentials,
            new UnusedRoutingStore(),
            new UnusedModelDiscovery(),
            new UnusedSubscriptionUsageCache(),
            new UnusedWorkspaceBindingValidator(),
            new UnusedWorkspaceDirectoryBrowser(),
            new UnusedVerificationPolicyCatalog(),
            NullLogger<NodeTransportClient>.Instance);
    }

    private sealed class UnusedRoutingStore : INodeRuntimeRoutingStore
    {
        public NodeRuntimeConfigurationMessage Current => throw new NotSupportedException();

        public Task<NodeRuntimeConfigurationMessage> UpdateAsync(
            UpdateNodeRuntimeConfigurationMessage update,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedModelDiscovery : IRuntimeModelDiscovery
    {
        public Task<IReadOnlyList<RuntimeModelCatalogMessage>> DiscoverAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedSubscriptionUsageCache : ISubscriptionUsageCache
    {
        public Task<NodeSubscriptionUsageMessage> GetAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedWorkspaceBindingValidator : IWorkspaceBindingValidator
    {
        public Task<WorkspaceBindingValidationResultMessage> ValidateAsync(
            WorkspaceBindingValidationRequestMessage request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedWorkspaceDirectoryBrowser : IWorkspaceDirectoryBrowser
    {
        public Task<WorkspaceDirectoryBrowseResponseMessage> BrowseAsync(
            WorkspaceDirectoryBrowseRequestMessage request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedVerificationPolicyCatalog : IVerificationPolicyCatalog
    {
        public VerificationPolicyCatalogMessage Capture() => throw new NotSupportedException();

        public VerificationProfileSelectionResultMessage ValidateSelection(
            VerificationProfileSelectionRequestMessage request)
            => throw new NotSupportedException();
    }
}
