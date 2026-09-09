using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using SmartSentinelEye.StreamDistribution.Application.Auth;
using SmartSentinelEye.StreamDistribution.Infrastructure.Auth;

namespace SmartSentinelEye.StreamDistribution.Infrastructure.Tests.Auth;

/// <summary>
/// Issue #2099 — adding the internal metadata-source constructor must not
/// change which constructor the container picks at
/// <c>StreamDistributionInfrastructureModule.cs:57</c>, where
/// <c>IWhepAuthValidator</c> is registered against this type.
///
/// <para>
/// <b>Why this is decisive rather than reassuring.</b> The container is given
/// <em>both</em> dependencies — <c>IOptions&lt;WhepAuthOptions&gt;</c> and an
/// <see cref="IConfigurationManager{T}"/>. If MS.DI considered non-public
/// constructors it would see two single-parameter constructors, both
/// satisfiable and neither a superset of the other, and throw on the ambiguity
/// instead of resolving. Resolution succeeding is therefore evidence that the
/// internal constructor is invisible to it, not merely that the public one
/// still works.
/// </para>
///
/// <para>
/// Registered exactly as the module registers it — a singleton against the
/// interface — so what is pinned is the production selection and not a
/// hand-rolled activation.
/// </para>
/// </summary>
public sealed class WhepAuthValidatorResolutionTests
{
    [Fact]
    public void The_container_resolves_the_validator_through_its_public_options_constructor()
    {
        ServiceCollection services = [];
        services.AddSingleton(
            Options.Create(new WhepAuthOptions
            {
                Authority = "https://keycloak.invalid/realms/smart-sentinel-eye",
            }));
        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(new UnusedMetadata());
        services.AddSingleton<IWhepAuthValidator, WhepAuthValidator>();

        using ServiceProvider provider = services.BuildServiceProvider();

        IWhepAuthValidator validator = provider.GetRequiredService<IWhepAuthValidator>();

        validator.ShouldBeOfType<WhepAuthValidator>(
            customMessage: "the container no longer resolves IWhepAuthValidator to a "
            + "WhepAuthValidator. The internal metadata-source constructor added for #2099 is "
            + "meant to be invisible to MS.DI; if it is not, the running service may be built "
            + "against a metadata source the composition root never intended.");
    }

    /// <summary>
    /// Present so the internal constructor's parameter is satisfiable. It is
    /// never called: if it were, this test would be exercising the constructor
    /// it exists to prove unreachable.
    /// </summary>
    private sealed class UnusedMetadata : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            throw new NotSupportedException(
                "the container built WhepAuthValidator through its internal metadata-source "
                + "constructor (#2099).");

        public void RequestRefresh() =>
            throw new NotSupportedException(
                "the container built WhepAuthValidator through its internal metadata-source "
                + "constructor (#2099).");
    }
}
