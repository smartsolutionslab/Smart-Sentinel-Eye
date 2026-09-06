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
    /// 200; the product targets 250 cameras <em>on the wall</em>, so this is
    /// roughly one or two requests. Only roughly: the lookup asks for retired
    /// cameras too (spec 083), and a decommissioned camera keeps its row, so
    /// the count grows with a fab's history rather than with the hardware
    /// currently installed in it.
    /// </summary>
    public int PageSize { get; set; } = 200;
}
