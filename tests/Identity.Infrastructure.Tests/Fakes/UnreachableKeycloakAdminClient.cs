using SmartSentinelEye.Identity.Application.KeycloakAdmin;

namespace SmartSentinelEye.Identity.Infrastructure.Tests.Fakes;

/// <summary>
/// A provider that cannot be reached: every call throws the transport failure
/// an unreachable Keycloak produces.
///
/// <para>
/// Hand-written rather than mocked (ADR-0054). It is deliberately not
/// <c>Identity.Application.Tests</c>' <c>FakeKeycloakAdminClient</c> — that
/// assembly cannot be referenced from here without giving Application's tests a
/// path to Infrastructure, and the seam this needs is one line.
/// </para>
/// </summary>
public sealed class UnreachableKeycloakAdminClient : IKeycloakAdminClient
{
    /// <summary>
    /// How many times the enumeration was attempted. Asserted, so a startup
    /// service that swallowed the failure by never asking in the first place is
    /// not mistaken for one that survived it.
    /// </summary>
    public int EnumerationAttempts { get; private set; }

    public Task<KeycloakClientCredentials> CreateClientAsync(
        KeycloakClientRepresentation representation,
        string fabGroupPath,
        CancellationToken cancellationToken) => throw Unreachable();

    public Task<KeycloakClientCredentials> RotateClientSecretAsync(
        string clientId, CancellationToken cancellationToken) => throw Unreachable();

    public Task<KeycloakClientCredentials> ReadClientSecretAsync(
        string clientId, CancellationToken cancellationToken) => throw Unreachable();

    public Task DisableClientAsync(
        string clientId, CancellationToken cancellationToken) => throw Unreachable();

    public Task<IReadOnlyList<string>> GetSubGroupNamesAsync(
        string parentPath, CancellationToken cancellationToken) => throw Unreachable();

    public Task<IReadOnlyList<string>> GetEnrolledKioskClientIdsAsync(
        CancellationToken cancellationToken)
    {
        EnumerationAttempts++;
        throw Unreachable();
    }

    public Task StripInheritedRealmRolesAsync(
        string clientId, CancellationToken cancellationToken) => throw Unreachable();

    private static HttpRequestException Unreachable() =>
        new("Keycloak is not reachable.");
}
