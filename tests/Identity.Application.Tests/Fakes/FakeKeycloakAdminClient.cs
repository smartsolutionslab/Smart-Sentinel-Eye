using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Identity.Application.Tests.Fakes;

/// <summary>
/// Test-side <see cref="IKeycloakAdminClient"/> that mirrors the
/// production contract: Create rejects duplicates with
/// <see cref="KeycloakClientAlreadyExistsException"/>, Rotate +
/// Disable throw <see cref="KeycloakClientNotFoundException"/>
/// when the client is unknown. <see cref="FailNextCall"/> lets
/// tests inject a transport failure to exercise the
/// <c>KEYCLOAK_UNAVAILABLE</c> error path.
/// </summary>
public sealed class FakeKeycloakAdminClient : IKeycloakAdminClient
{
    private readonly Dictionary<string, KeycloakClientRepresentation> _clients =
        new(StringComparer.Ordinal);

    public List<string> Disabled { get; } = [];

    /// <summary>
    /// Kiosk accounts whose inherited realm privileges have been taken away.
    ///
    /// <para>
    /// A <b>set</b>, so a test can assert the removal is idempotent without
    /// counting: sweeping twice must not change what it holds.
    /// </para>
    ///
    /// <para>
    /// <b>Which is also why a set alone cannot see a sweep doing more work</b>
    /// (#2151): repeated calls collapse into it. Use <see cref="StripCalls"/>
    /// when the count is the claim.
    /// </para>
    /// </summary>
    public HashSet<string> Stripped { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Every <see cref="StripInheritedRealmRolesAsync"/> call, in order,
    /// repeats included — the record <see cref="Stripped"/> discards.
    ///
    /// <para>
    /// Enrolment's own strip is deliberately not recorded here: it happens
    /// inside <see cref="CreateClientAsync"/> and is not a call any sweep made.
    /// </para>
    /// </summary>
    public List<string> StripCalls { get; } = [];

    /// <summary>Client ids for which the strip should fail, however it is reached.</summary>
    public HashSet<string> StripFailsFor { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CurrentSecrets { get; } = new(StringComparer.Ordinal);

    public string? FailNextCall { get; set; }
    public int CallCount { get; private set; }

    /// <summary>
    /// Every representation handed to <see cref="CreateClientAsync"/>, in order.
    ///
    /// <para>
    /// Recorded separately from the created-client map because this is what was
    /// <em>asked for</em>: a create whose strip fails removes the client again,
    /// and the request the handler composed is still the thing under test.
    /// <c>RuntimeClientAudienceTests</c> (spec 069) reads it — nothing else can
    /// see the scopes a runtime-created client is born with.
    /// </para>
    /// </summary>
    public List<KeycloakClientRepresentation> Created { get; } = [];

    public Task<KeycloakClientCredentials> CreateClientAsync(
        KeycloakClientRepresentation representation,
        string fabGroupPath,
        CancellationToken cancellationToken)
    {
        CallCount++;
        if (FailNextCall is not null)
        {
            ThrowAndClear();
        }

        Ensure.That(representation).IsNotNull();
        Created.Add(representation);
        if (_clients.ContainsKey(representation.ClientId))
        {
            throw new KeycloakClientAlreadyExistsException(representation.ClientId);
        }
        _clients.Add(representation.ClientId, representation);

        // **Production strips as part of creating, so this must too** (spec 052).
        // A fake that created an account and left the privilege on it would let
        // every test describe a system that does not exist — and the failure
        // path below is what proves an enrolment cannot report success over an
        // account still holding it.
        if (StripFailsFor.Contains(representation.ClientId))
        {
            // The real client removes the half-enrolled client before rethrowing,
            // so a retry is not blocked by a leftover.
            _clients.Remove(representation.ClientId);
            throw new InvalidOperationException(
                $"Keycloak refused to strip '{representation.ClientId}'.");
        }
        Stripped.Add(representation.ClientId);

        string secret = $"secret-{representation.ClientId}";
        CurrentSecrets[representation.ClientId] = secret;
        return Task.FromResult(new KeycloakClientCredentials(secret));
    }

    public Task<KeycloakClientCredentials> RotateClientSecretAsync(
        string clientId, CancellationToken cancellationToken)
    {
        CallCount++;
        if (FailNextCall is not null)
        {
            ThrowAndClear();
        }

        if (!_clients.ContainsKey(clientId))
        {
            throw new KeycloakClientNotFoundException(clientId);
        }
        string secret = $"secret-{clientId}-rotated";
        CurrentSecrets[clientId] = secret;
        return Task.FromResult(new KeycloakClientCredentials(secret));
    }

    /// <summary>
    /// Hands back the secret the client already has, without changing it — the
    /// distinction from <see cref="RotateClientSecretAsync"/> that ADR-0142's
    /// replay depends on. A fake that rotated here would let a broken replay
    /// pass, because the caller would still receive *a* working secret.
    /// </summary>
    public Task<KeycloakClientCredentials> ReadClientSecretAsync(
        string clientId, CancellationToken cancellationToken)
    {
        CallCount++;
        if (FailNextCall is not null)
        {
            ThrowAndClear();
        }

        if (!_clients.ContainsKey(clientId))
        {
            throw new KeycloakClientNotFoundException(clientId);
        }

        return Task.FromResult(new KeycloakClientCredentials(CurrentSecrets[clientId]));
    }

    public Task DisableClientAsync(string clientId, CancellationToken cancellationToken)
    {
        CallCount++;
        if (FailNextCall is not null)
        {
            ThrowAndClear();
        }

        Disabled.Add(clientId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sub-groups this fake will report, keyed by parent path. Spec 019 reads
    /// <c>/fabs</c> through this seam; Identity's own handlers never call it.
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> SubGroups { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> GetEnrolledKioskClientIdsAsync(
        CancellationToken cancellationToken)
    {
        CallCount++;
        if (FailNextCall is not null)
        {
            ThrowAndClear();
        }

        // Mirrors production: the set is derived from the attribute enrolment
        // stamps, not from a naming convention repeated here.
        IReadOnlyList<string> kiosks = _clients
            .Where(entry => entry.Value.Attributes is not null
                && entry.Value.Attributes.TryGetValue("sse.kind", out string? kind)
                && kind == "kiosk")
            .Select(entry => entry.Key)
            .ToArray();

        return Task.FromResult(kiosks);
    }

    public Task StripInheritedRealmRolesAsync(
        string clientId, CancellationToken cancellationToken)
    {
        CallCount++;
        StripCalls.Add(clientId);
        if (StripFailsFor.Contains(clientId))
        {
            throw new InvalidOperationException($"Keycloak refused to strip '{clientId}'.");
        }

        if (FailNextCall is not null)
        {
            ThrowAndClear();
        }

        Stripped.Add(clientId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetSubGroupNamesAsync(
        string parentPath, CancellationToken cancellationToken)
    {
        CallCount++;
        if (FailNextCall is not null)
        {
            ThrowAndClear();
        }

        return Task.FromResult(
            SubGroups.TryGetValue(parentPath, out IReadOnlyList<string>? names) ? names : []);
    }

    private void ThrowAndClear()
    {
        string message = FailNextCall!;
        FailNextCall = null;
        // Surface as HttpRequestException so the handlers' generic
        // catch-all (not OperationCanceledException) treats it as
        // a transport failure rather than a domain invariant
        // violation.
        throw new HttpRequestException(message);
    }
}
