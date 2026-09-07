using SmartSentinelEye.Identity.Application.KeycloakAdmin;

namespace SmartSentinelEye.Identity.Infrastructure.Tests.Fakes;

/// <summary>
/// A provider that answers the enumeration with a fixed set of enrolled kiosks
/// and accepts every strip.
///
/// <para>
/// Hand-written rather than mocked (ADR-0054), and deliberately not
/// <c>Identity.Application.Tests</c>' <c>FakeKeycloakAdminClient</c> — that
/// assembly cannot be referenced from here without giving Application's tests a
/// path to Infrastructure, which is the absence spec 092 relies on when it calls
/// <c>Does_not_touch_an_account_this_system_did_not_enrol</c> vacuous.
/// </para>
/// </summary>
public sealed class EnrolledKiosksKeycloakAdminClient(params string[] kiosks) : IKeycloakAdminClient
{
    /// <summary>
    /// How many times the enumeration was asked for. Asserted, so "nothing was
    /// logged" cannot be satisfied by a pass that never ran.
    /// </summary>
    public int EnumerationAttempts { get; private set; }

    public List<string> Stripped { get; } = [];

    public Task<IReadOnlyList<string>> GetEnrolledKioskClientIdsAsync(CancellationToken cancellationToken)
    {
        EnumerationAttempts++;
        return Task.FromResult<IReadOnlyList<string>>(kiosks);
    }

    public Task StripInheritedRealmRolesAsync(string clientId, CancellationToken cancellationToken)
    {
        Stripped.Add(clientId);
        return Task.CompletedTask;
    }

    public Task<KeycloakClientCredentials> CreateClientAsync(
        KeycloakClientRepresentation representation,
        string fabGroupPath,
        CancellationToken cancellationToken) => throw NotPartOfASweep();

    public Task<KeycloakClientCredentials> RotateClientSecretAsync(
        string clientId, CancellationToken cancellationToken) => throw NotPartOfASweep();

    public Task<KeycloakClientCredentials> ReadClientSecretAsync(
        string clientId, CancellationToken cancellationToken) => throw NotPartOfASweep();

    public Task DisableClientAsync(
        string clientId, CancellationToken cancellationToken) => throw NotPartOfASweep();

    public Task<IReadOnlyList<string>> GetSubGroupNamesAsync(
        string parentPath, CancellationToken cancellationToken) => throw NotPartOfASweep();

    private static NotSupportedException NotPartOfASweep() =>
        new("A sweep enumerates kiosks and strips them; it calls nothing else.");
}
