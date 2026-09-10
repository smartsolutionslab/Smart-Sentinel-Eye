namespace SmartSentinelEye.SystemVariables.Infrastructure.Resolution;

/// <summary>
/// The <c>system-variables-seeder</c> credential
/// <see cref="ReverseIndexSeederHostedService"/> presents to overlay-designer
/// at host start (spec 005 T061, delivered in spec 126).
///
/// <para>
/// A fifth spelling of the same four values, and deliberately so:
/// <c>ClientCredentialsTokenProvider</c> takes its credential through a
/// delegate rather than a shared options type precisely because each context
/// names them differently. Sharing one type here would be the abstraction that
/// forces the next context to rename its configuration to suit this one.
/// </para>
/// </summary>
public sealed class ReverseIndexSeederOptions
{
    public const string SectionName = "ReverseIndexSeeder";

    /// <summary>Keycloak realm base, e.g. <c>http://keycloak</c>.</summary>
    public string KeycloakUrl { get; set; } = string.Empty;

    public string Realm { get; set; } = "smart-sentinel-eye";

    public string ClientIdentifier { get; set; } = "system-variables-seeder";

    public string ClientSecret { get; set; } = string.Empty;
}
