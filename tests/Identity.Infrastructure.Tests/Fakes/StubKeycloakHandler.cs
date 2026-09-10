using System.Net;
using System.Text;

namespace SmartSentinelEye.Identity.Infrastructure.Tests.Fakes;

/// <summary>What one request carried, as the transport saw it.</summary>
public sealed record RecordedRequest(string Method, string PathAndQuery, string Body);

/// <summary>
/// A Keycloak that records what it was sent.
///
/// <para>
/// Hand-written rather than mocked (ADR-0054), and deliberately at the
/// <see cref="HttpMessageHandler"/> seam rather than at
/// <c>IKeycloakAdminClient</c>: the defect under test lives in
/// <c>HttpKeycloakAdminClient</c>'s private <c>JsonSerializerOptions</c>, so a
/// double standing in for that class cannot see it. <c>FakeKeycloakAdminClient</c>
/// is green throughout this defect's life for exactly that reason (#2207).
/// </para>
/// </summary>
public sealed class StubKeycloakHandler(
    Func<StubKeycloakHandler, RecordedRequest, string?> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> requests = [];

    public IReadOnlyList<RecordedRequest> Requests => requests;

    /// <summary>
    /// How many requests so far — this one included — have a path containing
    /// <paramref name="fragment"/>. Lets a responder answer the same URL
    /// differently on a second visit, which <c>CreateClientAsync</c>'s
    /// existence probe and its post-create re-probe require.
    /// </summary>
    public int CountOf(string fragment) =>
        requests.Count(request => request.PathAndQuery.Contains(fragment, StringComparison.Ordinal));

    public RecordedRequest Only(string method, string fragment) =>
        requests.Single(request => request.Method == method
            && request.PathAndQuery.Contains(fragment, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        RecordedRequest recorded = new(
            request.Method.Method,
            request.RequestUri!.PathAndQuery,
            body);
        requests.Add(recorded);

        string? payload = respond(this, recorded);
        return payload is null
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
    }
}
