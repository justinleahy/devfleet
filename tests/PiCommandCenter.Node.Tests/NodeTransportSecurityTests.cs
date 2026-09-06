using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Projects;
using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.SubscriptionUsage;

namespace PiCommandCenter.Node.Tests;

public sealed class NodeTransportSecurityTests
{
    [Theory]
    [InlineData("http://control.example.com")]
    [InlineData("https://")]
    [InlineData("https://node:secret@control.example.com")]
    [InlineData("ftp://localhost/control")]
    public async Task StartAsync_rejects_invalid_url_before_reading_credentials(string controlPlaneUrl)
    {
        await using var client = CreateClient(controlPlaneUrl);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartAsync(CancellationToken.None));

        Assert.Contains(nameof(NodeOptions.ControlPlaneUrl), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignalR_handler_disables_redirects_without_replacing_certificate_validation()
    {
        using var handler = new HttpClientHandler();

        var configuredHandler = NodeTransportClient.DisableAutomaticRedirects(handler);

        Assert.Same(handler, configuredHandler);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public async Task Completion_gateway_rejects_missing_verification_credential_before_transport()
    {
        await using var client = CreateClient("https://control.example.com");
        var gateway = new NodeTransportCompletionGateway(
            client,
            new StubAssignmentCredentialSource());
        var requestId = Guid.NewGuid();
        var run = new VerificationRunDto(
            Guid.NewGuid(),
            requestId,
            "default",
            "tests",
            VerificationRunStatus.Passed,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "passed",
            null,
            Mandatory: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.RecordVerificationRunAsync("root-session-17", run, CancellationToken.None));

        Assert.Contains("assignment credential", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_message_carries_the_exact_requesting_session()
    {
        var correlationId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        const string sessionId = "root-session-exact";
        var run = new VerificationRunDto(
            Guid.NewGuid(),
            requestId,
            "default",
            "tests",
            VerificationRunStatus.Passed,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "passed",
            null,
            Mandatory: true);

        var message = NodeTransportClient.CreateVerificationRunMessage(
            run,
            projectId,
            "claim-token",
            sessionId,
            correlationId);

        Assert.Equal(sessionId, message.SessionId);
        Assert.Equal(requestId, message.RequestId);
        Assert.Equal(projectId, message.ProjectId);
        Assert.Equal(correlationId, message.CorrelationId);
    }

    [Fact]
    public async Task Completion_gateway_rejects_project_mismatch_before_transport()
    {
        await using var client = CreateClient("https://control.example.com");
        var requestId = Guid.NewGuid();
        const string claimToken = "secret-claim-token";
        var credential = new NodeAssignmentCredential(
            requestId,
            Guid.NewGuid(),
            claimToken);
        var gateway = new NodeTransportCompletionGateway(
            client,
            new StubAssignmentCredentialSource(credential));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.BeginTerminalizationAsync(
                Guid.NewGuid(),
                requestId,
                "root-1",
                TerminalizationIntent.Complete,
                new CompletionEvidence("done", [], [], "passed"),
                null,
                CancellationToken.None));

        Assert.Contains("assignment credential", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(claimToken, exception.Message, StringComparison.Ordinal);
    }

    private static NodeTransportClient CreateClient(string controlPlaneUrl)
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
            Options.Create(new NodeOptions { ControlPlaneUrl = controlPlaneUrl }),
            credentials,
            new UnusedRoutingStore(),
            new UnusedModelDiscovery(),
            new UnusedSubscriptionUsageCache(),
            new UnusedWorkspaceBindingValidator(),
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

    private sealed class StubAssignmentCredentialSource(
        NodeAssignmentCredential? credential = null) : INodeAssignmentCredentialSource
    {
        public bool TryGetByRequest(
            Guid requestId,
            [NotNullWhen(true)] out NodeAssignmentCredential? result)
        {
            result = credential?.RequestId == requestId ? credential : null;
            return result is not null;
        }

        public bool TryGetByProject(
            Guid projectId,
            [NotNullWhen(true)] out NodeAssignmentCredential? result)
        {
            result = credential?.ProjectId == projectId ? credential : null;
            return result is not null;
        }
    }
}
