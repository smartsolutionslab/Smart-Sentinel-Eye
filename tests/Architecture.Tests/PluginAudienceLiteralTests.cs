using System.Text.RegularExpressions;
using SmartSentinelEye.ServiceDefaults;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Spec 090 FR-005 — the Mosquitto plugin's audience literal cannot drift from
/// the one the HTTP APIs validate.
///
/// <para>
/// <c>smart-sentinel-eye-api</c> is spelt four times, on purpose:
/// <see cref="AuthenticationDefaults.ApiAudience"/> (what the nine APIs and the
/// WHEP hook validate), <c>RealmAudienceTests</c> (what the realm file mints),
/// <c>BearerAudienceTests</c> (what the options the services build actually
/// contain), and now <c>apiAudience</c> in
/// <c>src/AppHost/mosquitto/plugin/jwt_auth.go</c>. The fourth is unavoidable —
/// Go cannot import a C# constant — and it is the one nobody would notice going
/// stale, because Go is not on this solution's build path at all.
/// </para>
///
/// <para>
/// <b>What going stale would cost.</b> Rename the audience and update the realm
/// and the services, and the plugin keeps requiring the old string: the realm
/// mints tokens naming the new one, the broker refuses every CONNECT, and MQTT
/// ingestion goes silently dark — the failure ADR-0100's 2026-08-06 addendum
/// records happening once already. <c>MqttAudienceIntegrationTests</c> catches
/// that too, but only in the Docker integration job and only as a
/// <c>CONNACK NotAuthorized</c>. This says which two files disagree, and says it
/// in the Docker-free job.
/// </para>
///
/// <para>
/// This reads the Go source as text because there is nothing else to read: the
/// constant exists only inside a file compiled by <c>mosquitto/Dockerfile</c>
/// stage 2, in a language this solution does not build. It is not a claim that
/// the broker checks anything — <c>MqttAudienceIntegrationTests</c> is what
/// observes that, against the running broker.
/// </para>
/// </summary>
public class PluginAudienceLiteralTests
{
    private const string PluginSource = "src/AppHost/mosquitto/plugin/jwt_auth.go";

    /// <summary>
    /// Matches the declaration this file exists to pin. Anchored on
    /// <c>const</c> so a comment mentioning the name, or a local shadowing it,
    /// is not mistaken for the declaration.
    /// </summary>
    private static readonly Regex Declaration = new(
        @"^\s*const\s+apiAudience\s*=\s*""(?<value>[^""]*)""\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void The_plugins_audience_is_the_one_the_apis_validate()
    {
        var root = RepositoryRoot();
        var file = Path.Combine(root.FullName, PluginSource.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(file).ShouldBeTrue(
            customMessage: $"{PluginSource} is missing. The Mosquitto JWT plugin is where the broker's "
            + "audience check lives; if the file moved, this test moves with it.");

        var match = Declaration.Match(File.ReadAllText(file));

        match.Success.ShouldBeTrue(
            customMessage: $"{RelativePath(root, file)} declares no `const apiAudience = \"...\"`. The "
            + $"plugin must require an audience (spec 090 FR-001), and it must be the one "
            + $"AuthenticationDefaults.ApiAudience names — '{AuthenticationDefaults.ApiAudience}'.");

        match.Groups["value"].Value.ShouldBe(AuthenticationDefaults.ApiAudience,
            customMessage: $"{RelativePath(root, file)} requires audience "
            + $"'{match.Groups["value"].Value}', while src/ServiceDefaults/AuthenticationDefaults.cs "
            + $"validates '{AuthenticationDefaults.ApiAudience}'. Go cannot import a C# constant, so "
            + "these two are held together here and nowhere else. While they disagree the realm mints "
            + "tokens the broker refuses, and every MQTT publisher and the event-ingestion subscriber "
            + "stops connecting.");
    }

    /// <summary>
    /// Reported with <c>/</c> throughout. <see cref="Path.GetRelativePath"/>
    /// returns the platform separator, so a backslash in an expected string is
    /// green on Windows and red on Linux CI — this repository has been bitten by
    /// exactly that.
    /// </summary>
    private static string RelativePath(DirectoryInfo root, string file) =>
        Path.GetRelativePath(root.FullName, file).Replace(Path.DirectorySeparatorChar, '/');

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null && !File.Exists(Path.Combine(candidate.FullName, "SmartSentinelEye.slnx")))
        {
            candidate = candidate.Parent;
        }

        return candidate
            ?? throw new InvalidOperationException(
                $"could not locate the repository root above {AppContext.BaseDirectory}");
    }
}
