using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;
using EventAggregate = SmartSentinelEye.EventIngestion.Domain.Event.Event;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Spec 101 (issues #587 + #588). Preserving a third-party payload is the
/// central promise of an ingestion context, and until this class every
/// EventIngestion integration test asserted row <em>counts</em>, existence, or a
/// single scalar — so a payload whose nested arrays were silently reordered, or
/// whose numbers were re-encoded, passed all of them.
///
/// <para>
/// <b>Two assertions, and neither is redundant.</b> Half A walks the fixture and
/// the stored payload together and names the path where they diverge; it is
/// blind to anything that changes only the <em>encoding</em>, because
/// <c>0.1000</c> and <c>0.1</c> compare equal as numbers. Half B compares the
/// stored bytes to a reviewed literal on disk and catches exactly that; on its
/// own it would report "two 4 KB strings differ at offset 1183".
/// </para>
///
/// <para>
/// <b>Half A compares object keys as a set, on purpose.</b> The column is
/// <c>jsonb</c> (<c>EventConfiguration.cs</c>), which re-sorts every object's
/// keys by (byte length, then bytewise) and would fail an order-sensitive
/// assertion on perfectly correct code. What <c>jsonb</c> <em>does</em> preserve
/// is array order and length at every depth — which is precisely the property
/// this test exists to protect. Issue #588 asked for "the JSONB column equals
/// the canonical form of the input byte-for-byte"; that is false for any payload
/// whose keys are not already in <c>jsonb</c>'s sort order, and a realistic
/// inference payload never is (spec 101 §2.4).
/// </para>
///
/// <para>
/// <b>The expectation is a checked-in file, never computed here.</b> Building it
/// at runtime from <c>Payload.From(fixture)</c> would prove that
/// <c>Payload.From</c> is deterministic and nothing whatever about the database.
/// It is derived by one reviewable rule: <c>canonical(fixture)</c> with every
/// object's keys sorted by (UTF-8 byte length, then ordinal), and nothing else
/// changed — which holds only because the fixture carries no exponent literals
/// and no duplicate keys.
/// </para>
///
/// <para>
/// No <c>Category</c> trait: one would take the class out of the CI
/// <c>integration</c> job's filter, which is a test CI never runs.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class InferencePayloadRoundTripIntegrationTests(AspireFixture aspire, ITestOutputHelper output)
{
    private const string Fab = "munich";

    // Inside the simulator's broker ACL (fab/munich/inference/#). A topic
    // outside it is discarded silently, which reads later as a lost event.
    private const string Topic = "fab/munich/inference/cam-0447";

    private const string FixtureName = "inference-sample.json";
    private const string ExpectedName = "inference-sample.expected.json";

    [Fact]
    public async Task Inference_payload_with_nested_arrays_survives_the_round_trip()
    {
        string fixtureJson = ReadFixture(FixtureName);
        // Trimmed: the checked-in file ends with a newline, as text files here
        // do. Canonical JSON has no leading or trailing whitespace of its own,
        // so this removes the editor's byte and none of the payload's.
        string expectedCanonical = ReadFixture(ExpectedName).Trim();

        Guid identifier = Guid.CreateVersion7();
        string kind = $"Inference{identifier:N}"[..24];
        output.WriteLine($"publishing {identifier} as kind {kind} to {Topic}");

        await new PlantFloor(aspire).PublishRawAsync(Envelope(identifier, kind, fixtureJson), Topic);

        Payload stored = await WaitForPayloadAsync(identifier, TimeSpan.FromSeconds(120));
        output.WriteLine($"stored payload is {stored.Value.Length} chars");

        using JsonDocument sent = JsonDocument.Parse(fixtureJson);
        using JsonDocument readBack = JsonDocument.Parse(stored.Value);

        // Half A — the meaning survived, and a failure says where.
        AssertSameJson(sent.RootElement, readBack.RootElement, "$");

        AssertStructureIsNotVacuous(readBack.RootElement, sent.RootElement);

        // Half B — the encoding did not drift.
        stored.Value.ShouldBe(
            expectedCanonical,
            "the stored payload no longer matches the reviewed canonical form in "
            + $"{ExpectedName}. That file is canonical({FixtureName}) with every object's keys "
            + "sorted by (UTF-8 byte length, then ordinal) and nothing else changed; a difference "
            + "means either the canonicaliser re-encoded a value or jsonb reshaped it.");
    }

    /// <summary>
    /// Guards against a pass that means nothing: "some JSON came back" would
    /// satisfy a comparison of a payload with itself. These are literals, not
    /// derived from the fixture at runtime.
    /// </summary>
    private static void AssertStructureIsNotVacuous(JsonElement readBack, JsonElement sent)
    {
        readBack.GetRawText().ShouldNotBe("{}", "the stored payload is the empty object");

        JsonElement detections = readBack.GetProperty("detections");
        detections.GetArrayLength().ShouldBe(11, "the stored payload lost or gained detections");
        sent.GetProperty("detections").GetArrayLength().ShouldBe(
            11, "the fixture itself changed — the literals below no longer describe it");

        detections[0].GetProperty("class").GetString().ShouldBe("person");
        detections[0].GetProperty("track").GetProperty("history").GetRawText().ShouldBe(
            "[[110,46],[111,47],[112,48]]",
            "the innermost nested array did not come back in the order it was sent");
        detections[10].GetProperty("class").GetString().ShouldBe(
            "cart", "the detections array came back in a different order");
    }

    private static void AssertSameJson(JsonElement sent, JsonElement readBack, string path)
    {
        readBack.ValueKind.ShouldBe(sent.ValueKind, $"value kind differs at {path}");

        switch (sent.ValueKind)
        {
            case JsonValueKind.Object:
                AssertSameObject(sent, readBack, path);
                break;
            case JsonValueKind.Array:
                AssertSameArray(sent, readBack, path);
                break;
            case JsonValueKind.String:
                readBack.GetString().ShouldBe(sent.GetString(), $"string differs at {path}");
                break;
            case JsonValueKind.Number:
                AsNumber(readBack).ShouldBe(AsNumber(sent), $"number differs at {path}");
                break;
            default:
                // True, False and Null carry no value beyond their kind, which
                // the assertion above already settled.
                break;
        }
    }

    /// <summary>
    /// Key <b>set</b>, not key order — jsonb legitimately re-sorts keys, so
    /// asserting their order would fail on correct code (spec 101 §4).
    /// </summary>
    private static void AssertSameObject(JsonElement sent, JsonElement readBack, string path)
    {
        string[] sentKeys = [.. sent.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];
        string[] readBackKeys = [.. readBack.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];
        readBackKeys.ShouldBe(sentKeys, $"key set differs at {path}");

        foreach (JsonProperty property in sent.EnumerateObject())
        {
            AssertSameJson(property.Value, readBack.GetProperty(property.Name), $"{path}.{property.Name}");
        }
    }

    private static void AssertSameArray(JsonElement sent, JsonElement readBack, string path)
    {
        readBack.GetArrayLength().ShouldBe(sent.GetArrayLength(), $"array length differs at {path}");

        int index = 0;
        foreach (JsonElement item in sent.EnumerateArray())
        {
            AssertSameJson(item, readBack[index], $"{path}[{index}]");
            index++;
        }
    }

    /// <summary>
    /// <c>decimal</c> rather than the raw text, so Half A stays a claim about
    /// meaning and Half B keeps its reason to exist. Exact for the 19-digit
    /// nanosecond stamp and for the fixed-scale fractions alike;
    /// <see cref="NumberStyles.Float"/> so a re-encoded value that arrives in
    /// exponent form fails as a mismatch rather than as a parse error.
    /// </summary>
    private static decimal AsNumber(JsonElement element) =>
        decimal.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// Read through the EF context, the same query and the same read converter
    /// as <c>EventRepository.GetByIdentifierAsync</c> — so what is asserted on
    /// is what a caller of the repository would get.
    /// </summary>
    private async Task<Payload> WaitForPayloadAsync(Guid identifier, TimeSpan timeout)
    {
        FabIdentifier fab = FabIdentifier.From(Fab);
        EventIdentifier eventIdentifier = EventIdentifier.From(identifier);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
            EventAggregate? found = await database.Events
                .Where(candidate => candidate.Fab == fab && candidate.Id == eventIdentifier)
                .FirstOrDefaultAsync();

            if (found is not null)
            {
                return found.Payload;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new InvalidOperationException(
            $"event {identifier} never reached the events table within {timeout.TotalSeconds:N0}s. "
            + $"The publish to {Topic} was accepted by the broker, so this is the ingest path, not "
            + "the ACL.");
    }

    /// <summary>
    /// Built by concatenation rather than serialisation so the fixture's own
    /// bytes go on the wire unaltered — the subscriber canonicalises the raw
    /// text of the <c>payload</c> property, and a re-serialisation here would
    /// quietly do half the job under test.
    /// </summary>
    private static string Envelope(Guid identifier, string kind, string payloadJson) =>
        "{\"eventId\":\"" + identifier + "\",\"kind\":\"" + kind + "\",\"occurredAt\":\""
        + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        + "\",\"payload\":" + payloadJson + "}";

    /// <summary>
    /// Read from the source tree, the idiom
    /// <c>IntegrationTestSelectionTests</c> already uses. No
    /// <c>CopyToOutputDirectory</c> item, so the csproj is untouched — and no
    /// ADR-0109 contention file with it.
    /// </summary>
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot().FullName,
            "tests", "Integration.Tests", "EventIngestion", "Fixtures", name));

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
