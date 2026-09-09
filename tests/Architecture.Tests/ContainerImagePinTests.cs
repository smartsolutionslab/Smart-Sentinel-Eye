using System.Text.RegularExpressions;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards that a container image the AppHost composes names a fixed upstream
/// release rather than a floating tag (issue #2103, spec 113).
///
/// <para>
/// MediaMTX is why this is a build failure and not a review note. It does not
/// merely run beside this code — it <b>composes inputs this code reads</b> and
/// decides outcomes this code relies on: the <c>token</c>/<c>path</c>/<c>action</c>
/// fields the WHEP authorization hook receives, <c>authHTTPExclude</c>'s meaning,
/// the refusal to publish to a path whose <c>source</c> is not <c>publisher</c>,
/// and the treatment of 401 as a challenge that made #2094 answer 403. None of
/// those four is written down in this repository, and on a floating tag all four
/// can change with no diff and no PR — arriving as a red CI run on a branch that
/// changed nothing, on whichever machine pulled first.
/// </para>
///
/// <para>
/// <b>This bans a category, never a value.</b> No assertion here names
/// <c>1.21.0-ffmpeg</c>. Bumping MediaMTX must not require editing its own guard:
/// a guard that obstructs the legitimate change it governs gets deleted within a
/// month, taking its protection with it — the reasoning
/// <see cref="FoundingDecisionRecordTests"/> spells out at length.
/// </para>
///
/// <para>
/// Reads the tree from disk. <c>Architecture.Tests</c> deliberately holds no
/// reference to the AppHost, whose Aspire hosting and DCP graph would turn a
/// suite that runs in seconds without Docker into one that does not —
/// <see cref="LogTailCoverageTests"/> and <c>IntegrationTestSelectionTests</c>
/// read source for the same reason.
/// </para>
/// </summary>
public class ContainerImagePinTests
{
    private const string AppHost = "src/AppHost/AppHost.cs";
    private const string ScriptTree = "scripts";
    private const string MediaMtxImage = "bluenviron/mediamtx";

    /// <summary>
    /// The three-argument <c>AddContainer(name, image, tag)</c>. A call whose tag
    /// is not a literal is not matched and so is not judged: it cannot be read
    /// from source, and failing it would demand a rewrite of correct code.
    /// </summary>
    private static readonly Regex ContainerDeclaration = new(
        @"AddContainer\(\s*""(?<name>[^""]+)""\s*,\s*""(?<image>[^""]+)""\s*,\s*""(?<tag>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex ImageTagCall = new(
        @"WithImageTag\(\s*""(?<tag>[^""]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// A MediaMTX reference written out for a human — in a comment explaining a
    /// cost, in prose, or on a <c>docker run</c> line in a script. These go stale
    /// silently when the containers move, which is how a reader ends up trusting
    /// a paragraph about an image nothing runs.
    /// </summary>
    private static readonly Regex WrittenReference = new(
        MediaMtxImage + @":(?<tag>[A-Za-z0-9][A-Za-z0-9._-]*)",
        RegexOptions.Compiled);

    [Fact]
    public void No_container_image_in_the_app_host_runs_a_floating_tag()
    {
        Pin[] pins = AppHostPins();

        pins.Length.ShouldBeGreaterThan(
            0,
            $"no literal image tag was found in {AppHost} — the scan is broken, not the code. A "
            + "source-scanning guard that matches nothing passes, and a passing guard that checks "
            + "nothing is indistinguishable from one that holds.");

        Pin[] floating = [.. pins.Where(pin => IsFloating(pin.Tag))];

        floating.ShouldBeEmpty(Explain(floating));
    }

    /// <summary>
    /// <c>fixture-video</c>'s block argues it costs the integration lane "no image
    /// pull at all, because it is the same tag the ungated <c>mediamtx</c> above
    /// already pulls". Three independent literals four hundred lines apart can
    /// drift, and when they do that argument becomes false quietly, with the cost
    /// paid in every integration run.
    /// </summary>
    [Fact]
    public void The_media_mtx_containers_all_name_one_release()
    {
        Pin[] mediaMtx = [.. AppHostPins().Where(pin => pin.Image == MediaMtxImage)];

        mediaMtx.Length.ShouldBeGreaterThanOrEqualTo(
            3,
            $"expected at least the three MediaMTX containers in {AppHost} — the SFU, fixture-video "
            + $"and camera-sim — and the scan found {mediaMtx.Length}. Either a container was removed "
            + "(update this number and say why) or the scan no longer matches how they are declared.");

        string[] tags = DistinctTags(mediaMtx);

        tags.Length.ShouldBe(
            1,
            $"the MediaMTX containers name {tags.Length} different tags ({string.Join(", ", tags)}):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, mediaMtx.Select(pin => $"  {AppHost}:{pin.Line} {pin.Name} → {pin.Tag}")));
    }

    /// <summary>
    /// The written references — comments and scripts — against what the
    /// containers run. <c>generate-sim-clips.sh</c> runs FFmpeg out of this same
    /// image because the host has none, so a bump that misses it regenerates the
    /// committed clips with a different encoder than the one that made them.
    /// </summary>
    [Fact]
    public void Every_written_media_mtx_reference_names_the_release_the_containers_run()
    {
        string[] declared = DistinctTags([.. AppHostPins().Where(pin => pin.Image == MediaMtxImage)]);

        declared.Length.ShouldBe(
            1,
            "the MediaMTX containers do not agree on one tag, so there is nothing to check written "
            + $"references against. {nameof(The_media_mtx_containers_all_name_one_release)} says which.");

        Pin[] written = WrittenReferences();

        written.Length.ShouldBeGreaterThan(
            0,
            $"no written '{MediaMtxImage}:<tag>' reference was found in {AppHost} or under "
            + $"{ScriptTree}/ — at least the clip-generation script carries one. The scan is broken.");

        Pin[] stale = [.. written.Where(pin => pin.Tag != declared[0])];

        stale.ShouldBeEmpty(
            $"the MediaMTX containers run '{declared[0]}', and these references name something else:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, stale.Select(pin => $"  {pin.Name}:{pin.Line} names '{pin.Tag}'")));
    }

    /// <summary>
    /// A tag is floating when any hyphen-separated segment is <c>latest</c>:
    /// <c>latest</c>, <c>latest-ffmpeg</c>, <c>latest-ffmpeg-rpi</c>. A
    /// major-only tag such as <c>4-management-alpine</c> is deliberately not
    /// caught — narrowing that is a decision about a different image, and this
    /// guard was not given one.
    /// </summary>
    private static bool IsFloating(string tag) =>
        tag.Split('-').Any(segment => segment.Equals("latest", StringComparison.OrdinalIgnoreCase));

    private static string Explain(Pin[] floating) =>
        $"{floating.Length} container image(s) in {AppHost} run a floating tag:"
        + Environment.NewLine
        + string.Join(
            Environment.NewLine,
            floating.Select(pin => $"  {AppHost}:{pin.Line} {pin.Name} → {pin.Image}:{pin.Tag}"))
        + Environment.NewLine
        + "Name the release instead. A floating tag changes what runs on the next pull, on whichever "
        + "machine pulls first, with no diff and no PR — so the failure surfaces as a red run on a "
        + "branch that changed nothing (#2103).";

    private static string[] DistinctTags(Pin[] pins) =>
        [.. pins.Select(pin => pin.Tag).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static Pin[] AppHostPins()
    {
        string[] lines = ReadLines(AppHost);
        List<Pin> pins = [];

        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match match in ContainerDeclaration.Matches(lines[i]))
            {
                pins.Add(new Pin(
                    match.Groups["name"].Value,
                    match.Groups["image"].Value,
                    match.Groups["tag"].Value,
                    i + 1));
            }

            foreach (Match match in ImageTagCall.Matches(lines[i]))
            {
                pins.Add(new Pin(WithImageTagOwner(lines, i), Image: string.Empty, match.Groups["tag"].Value, i + 1));
            }
        }

        return [.. pins];
    }

    /// <summary>
    /// <c>WithImageTag</c> sits several lines below the resource it configures,
    /// so the resource name is read back from the nearest preceding assignment.
    /// Failing to find one costs a less helpful message, never a wrong verdict.
    /// </summary>
    private static string WithImageTagOwner(string[] lines, int index)
    {
        for (int i = index; i >= 0 && index - i < 12; i--)
        {
            Match assignment = Regex.Match(lines[i], @"^var (?<name>[A-Za-z][A-Za-z0-9]*) = builder");
            if (assignment.Success)
            {
                return assignment.Groups["name"].Value;
            }
        }

        return "(unnamed resource)";
    }

    private static Pin[] WrittenReferences()
    {
        DirectoryInfo root = RepositoryRoot();

        string[] files =
        [
            AppHost,
            .. Directory
                .EnumerateFiles(Path.Combine(root.FullName, ScriptTree), "*", SearchOption.AllDirectories)
                .Select(file => Relative(root, file)),
        ];

        List<Pin> written = [];

        foreach (string file in files)
        {
            string[] lines = ReadLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in WrittenReference.Matches(lines[i]))
                {
                    written.Add(new Pin(file, MediaMtxImage, match.Groups["tag"].Value, i + 1));
                }
            }
        }

        return [.. written];
    }

    private static string[] ReadLines(string relativePath)
    {
        string path = Path.Combine(RepositoryRoot().FullName, relativePath);
        File.Exists(path).ShouldBeTrue(
            $"expected {relativePath} at {path} — if it moved, update this guard rather than deleting it.");
        return File.ReadAllLines(path);
    }

    /// <summary>
    /// Reported with <c>/</c> throughout. <see cref="Path.GetRelativePath"/>
    /// returns the platform separator, so a backslash in an expected string is
    /// green on Windows and red on Linux CI — this repository has been bitten by
    /// exactly that.
    /// </summary>
    private static string Relative(DirectoryInfo root, string file) =>
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

    /// <summary>
    /// One image reference read out of the tree: a container declaration, a
    /// <c>WithImageTag</c> call, or a reference written for a human.
    /// </summary>
    private sealed record Pin(string Name, string Image, string Tag, int Line);
}
