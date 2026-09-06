namespace SmartSentinelEye.StreamDistribution.Infrastructure.Attribution;

/// <summary>
/// Configuration for the one-time startup attribution of streams provisioned
/// before spec 016 (ADR-0116). Everything here exists to mint one
/// client_credentials token and call CameraCatalog once.
/// </summary>
public sealed class StreamFabAttributionOptions
{
    public const string SectionName = "StreamFabAttribution";

    /// <summary>Keycloak realm base, e.g. <c>http://keycloak</c>.</summary>
    public string KeycloakUrl { get; set; } = string.Empty;

    public string Realm { get; set; } = "smart-sentinel-eye";

    public string ClientIdentifier { get; set; } = "stream-distribution-attribution";

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// How many cameras to ask for per page. CameraCatalog caps a listing at
    /// 200. This listing spans every fab the service account holds, and the
    /// constitution targets 250 concurrent cameras <em>per fab</em> (§Scale),
    /// so it is already several requests at target scale. Retired cameras are
    /// included too (spec 083), and their rows are never removed — so the total
    /// grows with every fab's history, not with the hardware currently
    /// installed.
    /// </summary>
    public int PageSize { get; set; } = 200;
}
