namespace SmartSentinelEye.Integration.Tests;

/// <summary>
/// Characterises the MediaMTX image reference the application model resolves
/// (issue #2103, spec 113).
///
/// <para>
/// Three containers run MediaMTX — the SFU itself, <c>fixture-video</c> and
/// <c>camera-sim</c> — and they must run the <b>same</b> one. The
/// <c>fixture-video</c> block spends a paragraph arguing it costs the
/// integration lane "no image pull at all, because it is the same tag the
/// ungated <c>mediamtx</c> above already pulls". That argument is true only
/// while the references agree, and nothing but this test says so: three
/// independent literals four hundred lines apart drift silently, and the cost
/// of the drift is paid in every integration run rather than reported.
/// </para>
///
/// <para>
/// <b>No assertion here names a tag.</b> This is a characterisation test in the
/// ADR-0144 sense: it was observed green before spec 113 pinned the image and
/// must pass unmodified after, so it asserts the invariant (one reference across
/// all of them) rather than the value, which the pin moves from
/// <c>latest-ffmpeg</c> to a release. An assertion that had to be edited by the
/// change it guards is evidence the behaviour moved — and pinning to the digest
/// already running moves none.
/// </para>
///
/// <para>
/// Builds the application model only: <c>CreateAsync</c> starts no resources, so
/// this costs no container and is safe beside a live stack — the same reasoning
/// <see cref="AppHostE2ESwitchTests"/> gives, and why the class is Docker-free
/// and carries the trait that says so.
/// </para>
/// </summary>
[Trait("Category", "FixtureLogic")]
public class AppHostMediaMtxImageTests
{
    private const string MediaMtxImage = "bluenviron/mediamtx";

    private static readonly string[] RunModeArguments =
    [
        "Parameters:PostgresUser=postgres",
        "Parameters:PostgresPassword=testpassword",
        "Parameters:KeycloakPassword=testkeycloak",
        "Parameters:RabbitMqPassword=testmessaging",
    ];

    private static readonly string[] FixtureArguments =
    [
        .. RunModeArguments,
        "E2ETests=true",
    ];

    /// <summary>
    /// A developer's <c>aspire run</c>, and the shape the end-to-end job boots
    /// bar its simulator switch: all three MediaMTX containers are composed.
    /// </summary>
    [Fact]
    public async Task A_run_mode_stack_resolves_every_media_mtx_container_to_one_image()
    {
        Container[] containers = await MediaMtxContainersAsync(RunModeArguments);

        Named(containers).ShouldContain("mediamtx");
        Named(containers).ShouldContain("fixture-video");
        Named(containers).ShouldContain("camera-sim");

        AllShareOneReference(containers);
    }

    /// <summary>
    /// The integration fixture's shape. <c>camera-sim</c> is dev-only and gone;
    /// <c>fixture-video</c> stays, because the fixture reads its RTSP path to
    /// watch a camera reach <c>Healthy</c> (spec 076, #198).
    /// </summary>
    [Fact]
    public async Task The_integration_fixture_shape_resolves_every_media_mtx_container_to_one_image()
    {
        Container[] containers = await MediaMtxContainersAsync(FixtureArguments);

        Named(containers).ShouldContain("mediamtx");
        Named(containers).ShouldContain("fixture-video");

        AllShareOneReference(containers);
    }

    /// <summary>
    /// The comparison both facts rest on. A model resolving no MediaMTX
    /// container at all yields no distinct references, which fails here rather
    /// than passing quietly — a scan that matches nothing is the one way a test
    /// like this holds while checking nothing.
    /// </summary>
    private static void AllShareOneReference(Container[] containers)
    {
        string[] references = [.. containers.Select(container => container.ImageReference).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        references.Length.ShouldBe(
            1,
            $"the {containers.Length} MediaMTX container(s) in this model resolve {references.Length} "
            + $"distinct image reference(s) — {string.Join(", ", references)}. They must run one image: "
            + "fixture-video's block argues it costs the integration lane no pull because it is the same "
            + "image the SFU already pulls, and that argument is only as true as this assertion.");
    }

    private static string[] Named(Container[] containers) =>
        [.. containers.Select(container => container.Name)];

    private static async Task<Container[]> MediaMtxContainersAsync(string[] arguments)
    {
        using IDistributedApplicationTestingBuilder builder =
            await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.SmartSentinelEye_AppHost>(arguments);

        return
        [
            .. builder.Resources
                .SelectMany(
                    resource => resource.Annotations.OfType<ContainerImageAnnotation>(),
                    (resource, image) => new Container(resource.Name, Reference(image)))
                .Where(container => container.ImageReference.StartsWith(MediaMtxImage, StringComparison.Ordinal)),
        ];
    }

    /// <summary>
    /// Rendered the way a reader would write it, so a failure message can be
    /// pasted into <c>docker pull</c>. A digest pin and a tag pin are both
    /// representable — the point is that whichever is used, all three agree.
    /// </summary>
    private static string Reference(ContainerImageAnnotation image) =>
        image.SHA256 is null
            ? $"{image.Image}:{image.Tag}"
            : $"{image.Image}@sha256:{image.SHA256}";

    private sealed record Container(string Name, string ImageReference);
}
