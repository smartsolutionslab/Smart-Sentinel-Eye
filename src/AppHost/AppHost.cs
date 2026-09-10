// Aspire composition root for Smart Sentinel Eye (ADR-0024).
//
// Resources wired here:
// - postgres: per-context database "camera-catalog-db" + Keycloak's own "keycloak-db"
// - rabbitmq: shared broker with management plugin in dev
// - keycloak: realm "smart-sentinel-eye" imported from Realms/ folder
// - migrations: MigrationRunner that runs once on startup
// - one project per bounded context, each WithReference()'d to the resources it consumes
//
// Test mode (E2ETests=true) makes the containers ephemeral; dev mode pins
// persistent lifetimes + data volumes so the stack survives restarts.

var builder = DistributedApplication.CreateBuilder(args);
bool isRunMode = builder.ExecutionContext.IsRunMode;
bool isE2ETests = bool.TryParse(builder.Configuration["E2ETests"], out bool e2e) && e2e;
// ADR-0111 records the Scenario Simulator as dev-only. `E2ETests` alone never
// delivered that: the end-to-end job boots with a plain `dotnet run` — run
// mode, flag unset — so the simulator has started there for as long as it has
// existed (#2013). It cannot simply join the `E2ETests` gate, because that flag
// also removes the three Vite apps the Playwright suite drives and spec 056's
// `fixture-video`. Absent or unparseable means a developer's `aspire run`, the
// only place it is wanted.
bool isScenarioSimulatorEnabled =
    !bool.TryParse(builder.Configuration["ScenarioSimulator"], out bool simulator) || simulator;

var postgresUser = builder.AddParameter("PostgresUser", "postgres");
var postgresPassword = builder.AddParameter("PostgresPassword", "dev-only-postgres-password", secret: true);
var keycloakPassword = builder.AddParameter("KeycloakPassword", "dev-only-keycloak-admin", secret: true);
// Mirrors the `identity-admin` client secret seeded in
// Realms/smart-sentinel-eye-realm.json. The Identity API reads it as
// `Keycloak:AdminClientSecret` to mint the realm-management
// service-account token (spec 008 ADR-0100).
var identityAdminClientSecret = builder.AddParameter("IdentityAdminClientSecret", "dev-only-identity-admin-secret", secret: true);
// Spec 019: the migration job reads /fabs to provision an events partition per
// fab. Its own client, read-only, holding query-groups + view-users — the
// working minimum, since group-by-path answers 403 with query-groups alone
// (verified against Keycloak 26.5). Wider than intended and still far narrower
// than identity-admin, which can create clients and users.
var migrationRunnerClientSecret = builder.AddParameter("MigrationRunnerClientSecret", "dev-only-migration-runner-secret", secret: true);
var rabbitPassword = builder.AddParameter("RabbitMqPassword", "dev-only-rabbit-password", secret: true);
// Mirrors the dev-only `scenario-simulator` confidential client seeded in
// Realms/smart-sentinel-eye-realm.json. The Scenario Simulator worker
// (ADR-0111) reads it as `ScenarioSimulator:Runtime:ClientSecret` to mint a
// client_credentials token (scope sse.cameras.write) for seeding the catalog.
var scenarioSimulatorClientSecret = builder.AddParameter("ScenarioSimulatorClientSecret", "dev-only-scenario-simulator-secret", secret: true);
// Mirrors the dev-only `event-ingestion` confidential client seeded in
// Realms/smart-sentinel-eye-realm.json. The MQTT subscriber mints a
// client_credentials token and presents it as its broker password; the
// go-auth plugin (ADR-0100) enforces azp == the MQTT username.
var eventIngestionMqttClientSecret = builder.AddParameter("EventIngestionMqttClientSecret", "dev-only-event-ingestion-secret", secret: true);
// Mirrors the `stream-distribution-attribution` confidential client seeded in
// Realms/smart-sentinel-eye-realm.json. StreamDistribution reads it as
// `StreamFabAttribution:ClientSecret` to mint a client_credentials token
// (scope sse.cameras.read) for the one-time startup attribution of streams
// provisioned before spec 016 (ADR-0116).
var streamDistributionAttributionClientSecret = builder.AddParameter("StreamDistributionAttributionClientSecret", "dev-only-stream-distribution-secret", secret: true);
// Mirrors the `system-variables-seeder` confidential client seeded in
// Realms/smart-sentinel-eye-realm.json. SystemVariables reads it as
// `ReverseIndexSeeder:ClientSecret` to mint a client_credentials token
// (scope sse.overlays.read) for the startup seed of the reverse index —
// the half of spec 005 T061 that never shipped (#2158, spec 126).
var systemVariablesSeederClientSecret = builder.AddParameter("SystemVariablesSeederClientSecret", "dev-only-system-variables-seeder-secret", secret: true);

// Spec 009 ADR-0101: the postgres image carries the timescaledb
// extension so the audit-observability hypertable + compression
// work. We use the single-node `timescale/timescaledb` community
// image rather than the `-ha` (Spilo/Patroni) variant: it is far
// lighter for dev/CI and still provides hypertables AND compression
// (a TSL feature the audit migration requires). The `-oss` tags
// drop compression, so the community (non-oss) tag is required.
// Every other context's database remains plain Postgres tables on
// the same server.
var postgres = builder
    .AddPostgres("postgres", userName: postgresUser, password: postgresPassword)
    .WithImage("timescale/timescaledb")
    .WithImageTag("2.27.1-pg17")
    // Postgres defaults to 100, which nine contexts sharing one container had
    // already almost spent: measured at 97 idle connections before any load.
    // That is not a theoretical margin — driving audit ingest at 100 ev/s took
    // the cluster past the limit and system-variables started refusing writes
    // with "53300: sorry, too many clients already", which reads as a bug in
    // whichever service happens to ask next rather than as a shared budget
    // running out (ADR-0124, ADR-0125).
    //
    // 500 is PostgresConnectionBudget.ServerMaxConnections. It is written out
    // rather than referenced because an Aspire project reference exposes no
    // assembly to the AppHost, and dragging ServiceDefaults in for one integer
    // costs more than it saves. The two are held together by
    // PostgresConnectionBudgetTests, which asks the *running* server what it
    // allows and fails if it is under the budget — a stronger check than
    // sharing a constant, because it verifies what was deployed rather than
    // what was written.
    .WithArgs("-c", "max_connections=500");

if (isRunMode && !isE2ETests)
{
    postgres
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume()
        .WithPgAdmin();
}

var cameraCatalogDb = postgres.AddDatabase("camera-catalog-db");
var streamDistributionDb = postgres.AddDatabase("stream-distribution-db");
var layoutCompositionDb = postgres.AddDatabase("layout-composition-db");
var overlayDesignerDb = postgres.AddDatabase("overlay-designer-db");
var systemVariablesDb = postgres.AddDatabase("system-variables-db");
var eventIngestionDb = postgres.AddDatabase("event-ingestion-db");
var automationDb = postgres.AddDatabase("automation-db");
var identityDb = postgres.AddDatabase("identity-db");
var auditDb = postgres.AddDatabase("audit-db");

var rabbitmq = builder
    .AddRabbitMQ("rabbitmq", password: rabbitPassword)
    .WithImageTag("4-management-alpine")
    .WithManagementPlugin();

if (isRunMode && !isE2ETests)
{
    rabbitmq
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume();
}

var keycloak = builder
    .AddKeycloak("keycloak", adminPassword: keycloakPassword)
    .WithRealmImport("../AppHost/Realms");

if (isRunMode && !isE2ETests)
{
    keycloak
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume();
}

// MediaMTX SFU brings RTSP ingest + WHEP playback (spec 002 T003, ADR-0011).
// MediaMTX is the runtime source of truth for live paths; the stream-distribution
// service is the durable source of truth and reconciles paths on startup.
//
// Pinned, and pinned harder than a typical image, because MediaMTX composes
// inputs this repository's code reads and decides outcomes it relies on: the
// `token`/`path`/`action` fields the WHEP authorization hook receives, what
// `authHTTPExclude` means in mediamtx.yml, the refusal to publish to a path
// whose `source` is not `publisher` (the safety that makes #2094's remaining
// gap unexploitable), and the treatment of 401 as a challenge that made #2094
// answer 403. None of the four is written down here, and on a floating tag all
// four could change with no diff and no PR — arriving as a red run on a branch
// that changed nothing, on whichever machine pulled first (#2103, spec 113).
//
// 1.21.0-ffmpeg is sha256:9d148b5f29906618dee627ea3232c75d4ed60da8d40a3ad7276ca87c1ec4f19e,
// the image `latest-ffmpeg` resolved to when it was pinned, so the pin changed
// the running bytes not at all. The version is named rather than the digest
// because every image this repository pins, it pins by version tag, and because
// a reader reasoning about MediaMTX's behaviour needs a number they can take to
// a changelog; the digest is recorded so the exact artifact stays recoverable.
// Bumping is by hand — no Dependabot or Renovate config exists in this tree.
// ContainerImagePinTests fails the build when any of this drifts apart.
var mediamtx = builder
    .AddContainer("mediamtx", "bluenviron/mediamtx", "1.21.0-ffmpeg")
    .WithBindMount("Resources/mediamtx.yml", "/mediamtx.yml")
    .WithHttpEndpoint(targetPort: 9997, name: "api")
    // Spec 024: the SFU measures its own RTP ingest; exposing the endpoint is
    // what makes camera → SFU readable at all (§VII, ≤ 80 ms).
    .WithHttpEndpoint(targetPort: 9998, name: "metrics")
    .WithHttpEndpoint(targetPort: 8889, name: "whep")
    .WithEndpoint(targetPort: 8554, name: "rtsp", scheme: "tcp");

// fixture-video (spec 056, spec 076) — one looping clip over RTSP, so an
// automated check can see a picture. Before spec 056 every overlay fixture
// pointed its camera at an address nothing served, so the tiles rendered
// `WHEP returned 404` and a tile that drew its label ONLY when the video
// failed passed the whole suite.
//
// **Gated `isRunMode` — deliberately NOT `camera-sim`'s gate**, which since
// #2013 carries a third conjunct (`isScenarioSimulatorEnabled`). The
// end-to-end job needs a picture and does not need a simulator, so the two
// blocks must not be folded together: adding that conjunct here would delete
// spec 056's video source from CI, which is exactly what
// `The_simulator_argument_leaves_the_web_apps_and_the_fixture_video_in_place`
// exists to prevent.
//
// **Both lanes get this, and the `!isE2ETests` conjunct is gone (spec 076,
// #198).** `E2ETests` is set by the *integration* fixture (`AspireFixture`)
// and by `AppHostE2ESwitchTests`, but not by the end-to-end stack boot, which
// is a plain `dotnet run` in `ci.yml`. The exclusion was right while nothing
// in the integration lane consumed the picture — an integration run has no
// browser, so it would otherwise have paid for a container, a 46 MB bind
// mount and a permanently looping FFmpeg it never read. That condition is
// gone: the integration lane now reads this source.
// `AspireFixture.RtspTestSourceUrl` points a camera at
// `rtsp://fixture-video:8554/loop`, which is how a test observes a stream
// reaching `Healthy` rather than only the failure half. What the integration
// run now pays is one container start plus a `-c copy` FFmpeg loop against an
// already-H.264 clip, so nothing transcodes — and **no image pull at all**,
// because `bluenviron/mediamtx:1.21.0-ffmpeg` is the same tag the ungated
// `mediamtx` above already pulls.
// `E2ETests_argument_excludes_the_dev_only_resources` now asserts the
// presence, so folding this back under `!isE2ETests` fails there.
if (isRunMode)
{
    builder
        .AddContainer("fixture-video", "bluenviron/mediamtx", "1.21.0-ffmpeg")
        .WithBindMount("Resources/fixture-video.yml", "/mediamtx.yml")
        // The whole clips directory, mounted where the config expects it.
        .WithBindMount("Resources/clips", "/media")
        .WithEndpoint(targetPort: 8554, name: "rtsp", scheme: "tcp");
}

if (isRunMode && !isE2ETests)
{
    // WebRTC media (ICE) must reach the host browser, which can't route to the
    // container IP MediaMTX advertises by default. Aspire's DCP proxies container
    // endpoints on random TCP host ports, which ICE can't use (the advertised
    // candidate port wouldn't match and UDP wouldn't traverse), so publish the
    // ICE mux directly with a raw docker port map (host 8189 -> container 8189,
    // UDP + TCP) and advertise the host loopback. Dev-only; prod browsers share
    // an L2 with the SFU (spec 002), so local candidates suffice.
    mediamtx
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerRuntimeArgs("--publish", "8189:8189/udp", "--publish", "8189:8189/tcp")
        .WithEnvironment("MTX_WEBRTCADDITIONALHOSTS", "127.0.0.1");
}


// Mosquitto MQTT broker for spec 006 EventIngestion (ADR-0095). Each
// PLC and inference device publishes on a per-device topic; the
// event-ingestion service subscribes with a fab-scoped wildcard.
//
// Spec 008 ADR-0100: the image is built from `mosquitto/Dockerfile`,
// which adds a custom Go auth plugin (`mosquitto/plugin/`) that
// validates Keycloak-minted RS256 JWTs against the realm JWKS with no
// per-CONNECT round-trip. The upstream iegomez/mosquitto-go-auth
// plugin can't do this (HMAC-only signatures, no JWKS) — see the ADR.
// Non-JWT passwords fall through to the password_file, so the spec 006
// EventIngestion subscriber + station-4 / camera-12 seeds keep working.
//
// The plugin needs the realm JWKS, so AppHost injects the
// container-reachable Keycloak URL as SSE_JWT_JWKS_URI and waits for
// Keycloak so the set is fetchable on first connect (the plugin also
// retries until it is).
var mosquitto = builder
    .AddDockerfile("mosquitto", "mosquitto")
    .WithBindMount("mosquitto/mosquitto.conf", "/mosquitto/config/mosquitto.conf")
    .WithBindMount("mosquitto/passwords.txt", "/mosquitto/config/passwords.txt")
    .WithBindMount("mosquitto/acl.txt", "/mosquitto/config/acl.txt")
    .WithEndpoint(targetPort: 1883, name: "mqtt", scheme: "tcp")
    .WithEnvironment(context =>
    {
        context.EnvironmentVariables["SSE_JWT_JWKS_URI"] = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/smart-sentinel-eye/protocol/openid-connect/certs");
    })
    .WaitFor(keycloak);

if (isRunMode && !isE2ETests)
{
    mosquitto
        .WithLifetime(ContainerLifetime.Persistent)
        .WithVolume("mosquitto-data", "/mosquitto/data");
}

// MinIO object storage (ADR-0009) — used by AuditObservability
// (spec 009 ADR-0101) for the per-chunk cold archive once a
// hypertable chunk crosses the 90-day boundary. The
// CommunityToolkit.Aspire.Hosting.Minio integration injects a
// `ConnectionStrings:minio` value into every consumer that
// `WithReference`s it; the Infrastructure project resolves an
// `IMinioClient` from that via `AddMinioClient("minio")`.
var minio = builder.AddMinioContainer("minio");

if (isRunMode && !isE2ETests)
{
    minio
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume();
}

// MigrationRunner orchestrates all per-context migrations and exits (ADR-0067).
var migrations = builder
    .AddProject<Projects.SmartSentinelEye_MigrationRunner>("migrations")
    .WithReference(cameraCatalogDb)
    .WithReference(streamDistributionDb)
    .WithReference(layoutCompositionDb)
    .WithReference(overlayDesignerDb)
    .WithReference(systemVariablesDb)
    .WithReference(eventIngestionDb)
    .WithReference(automationDb)
    .WithReference(identityDb)
    .WithReference(auditDb)
    .WaitFor(cameraCatalogDb)
    .WaitFor(streamDistributionDb)
    .WaitFor(layoutCompositionDb)
    .WaitFor(overlayDesignerDb)
    .WaitFor(systemVariablesDb)
    .WaitFor(eventIngestionDb)
    .WaitFor(automationDb)
    .WaitFor(identityDb)
    .WaitFor(auditDb)
    // Spec 019: partitions are provisioned per fab, and the fabs come from the
    // realm. WaitFor rather than a bare reference — an unreachable realm makes
    // the run fail (FR-011), so waiting is what keeps the ordinary case from
    // looking like the failure case.
    .WithReference(keycloak)
    .WithEnvironment("Keycloak__AdminClientId", "migration-runner")
    .WithEnvironment("Keycloak__AdminClientSecret", migrationRunnerClientSecret)
    .WaitFor(keycloak)
    // Dashboard grouping: nest the one-shot migration runner under postgres (the
    // data tier) — it migrates every per-context database, then exits.
    .WithParentRelationship(postgres);

var cameraCatalog = builder
    .AddProject<Projects.SmartSentinelEye_CameraCatalog_Api>("camera-catalog")
    .WithHttpEndpoint()
    .WithReference(cameraCatalogDb)
    .WithReference(rabbitmq)
    .WithReference(keycloak)
    .WaitFor(rabbitmq)
    .WaitFor(keycloak);

var streamDistribution = builder
    .AddProject<Projects.SmartSentinelEye_StreamDistribution_Api>("stream-distribution")
    .WithHttpEndpoint()
    .WithReference(streamDistributionDb)
    .WithReference(rabbitmq)
    .WithReference(keycloak)
    .WithReference(mediamtx.GetEndpoint("api"))
    .WithReference(mediamtx.GetEndpoint("whep"))
    // The one cross-context HTTP call this service makes, and only at startup:
    // a stream provisioned before spec 016 has no fab, and its camera is in
    // another database (ADR-0116). Not a WaitFor — attribution must not gate
    // host start, and an unreachable CameraCatalog simply leaves those streams
    // unattributed and therefore invisible.
    .WithReference(cameraCatalog)
    .WithEnvironment("StreamFabAttribution__ClientSecret", streamDistributionAttributionClientSecret)
    .WaitFor(rabbitmq)
    .WaitFor(keycloak)
    .WaitFor(mediamtx);

// In run mode the stream-distribution service runs as a host process, so the
// mediamtx container can't reach it via the `stream-distribution` service name
// baked into mediamtx.yml's authHTTPAddress (that DNS name resolves only among
// containers on the AppHost network). Override the WHEP auth hook with the
// Aspire-resolved, container-reachable endpoint so every WHEP open can be
// authorized; without it MediaMTX's auth callback fails with "no such host" and
// returns 401 to the browser. Publish mode containerizes stream-distribution, so
// the baked-in service name is correct there and this override is run-only.
if (isRunMode)
{
    mediamtx.WithEnvironment(context =>
    {
        context.EnvironmentVariables["MTX_AUTHHTTPADDRESS"] =
            ReferenceExpression.Create($"{streamDistribution.GetEndpoint("http")}/streams/authorize");
    });
}

var layoutComposition = builder
    .AddProject<Projects.SmartSentinelEye_LayoutComposition_Api>("layout-composition")
    .WithHttpEndpoint()
    .WithReference(layoutCompositionDb)
    .WithReference(rabbitmq)
    .WithReference(keycloak)
    // The one cross-context HTTP call this service makes, and only on the
    // write path: a tile's camera must be in its layout's fab (spec 017
    // FR-014), and only CameraCatalog knows a camera's fab. It carries the
    // caller's own token, so no service account is involved — unlike
    // ADR-0116's, this exception needs no standing credential.
    //
    // Not a WaitFor: a CameraCatalog outage must stop layout *authoring*
    // only. Reading layouts, the hub pushes and video all carry on.
    .WithReference(cameraCatalog)
    .WaitFor(rabbitmq)
    .WaitFor(keycloak);
var eventIngestion = builder
    .AddProject<Projects.SmartSentinelEye_EventIngestion_Api>("event-ingestion")
    .WithHttpEndpoint()
    .WithReference(eventIngestionDb)
    .WithReference(rabbitmq)
    .WithReference(keycloak)
    .WithReference(mosquitto.GetEndpoint("mqtt"))
    .WithEnvironment("Mosquitto__ClientSecret", eventIngestionMqttClientSecret)
    // Spec 020 FR-005. Five minutes is the shipped default, sized for a plant
    // whose failover takes minutes. Ninety seconds here so the bounded escape
    // is something a developer can actually watch happen — at five minutes
    // nobody ever sees a dead letter written, and an escape nobody has seen
    // work is an escape nobody knows is broken.
    .WithEnvironment("EventIngestion__IngestRetry__MaximumRetryWindow", "00:01:30")
    .WaitFor(rabbitmq)
    .WaitFor(keycloak)
    .WaitFor(mosquitto);
var overlayDesigner = builder
    .AddProject<Projects.SmartSentinelEye_OverlayDesigner_Api>("overlay-designer")
    .WithHttpEndpoint()
    .WithReference(overlayDesignerDb)
    .WithReference(rabbitmq)
    .WithReference(keycloak)
    .WaitFor(rabbitmq)
    .WaitFor(keycloak);
var systemVariables = builder
    .AddProject<Projects.SmartSentinelEye_SystemVariables_Api>("system-variables")
    .WithHttpEndpoint()
    .WithReference(systemVariablesDb)
    .WithReference(rabbitmq)
    .WithReference(keycloak)
    .WithReference(overlayDesigner)
    .WithEnvironment("ReverseIndexSeeder__ClientSecret", systemVariablesSeederClientSecret)
    .WaitFor(rabbitmq)
    .WaitFor(keycloak)
    .WaitFor(overlayDesigner);
var automation = builder
    .AddProject<Projects.SmartSentinelEye_Automation_Api>("automation")
    .WithHttpEndpoint()
    .WithReference(automationDb)
    .WithReference(rabbitmq)
    .WithReference(keycloak)
    .WaitFor(rabbitmq)
    .WaitFor(keycloak);
var identity = builder
    .AddProject<Projects.SmartSentinelEye_Identity_Api>("identity")
    .WithHttpEndpoint()
    .WithReference(identityDb)
    .WithReference(rabbitmq)
    .WithReference(keycloak)
    .WithEnvironment("Keycloak__AdminClientSecret", identityAdminClientSecret)
    .WaitFor(rabbitmq)
    .WaitFor(keycloak);
var auditObservability = builder
    .AddProject<Projects.SmartSentinelEye_AuditObservability_Api>("audit-observability")
    .WithHttpEndpoint()
    .WithReference(auditDb)
    .WithReference(rabbitmq)
    .WithReference(keycloak)
    .WithReference(minio)
    .WithEnvironment("Minio__Bucket", "audit-archive")
    .WaitFor(rabbitmq)
    .WaitFor(keycloak)
    .WaitFor(minio);

if (isE2ETests)
{
    // Ephemeral containers mean an empty database every run, so services must
    // not boot until the schema exists — Wolverine builds its outbox storage on
    // startup (AutoBuildMessageStorageOnStartup). This is also the services'
    // only ordering against the data tier: WithReference injects a connection
    // string but waits for nothing. Dev reuses a migrated volume, where gating
    // would only cost startup time.
    foreach (IResourceBuilder<ProjectResource> dependent in new[]
    {
        cameraCatalog, streamDistribution, layoutComposition, eventIngestion,
        overlayDesigner, systemVariables, automation, identity, auditObservability,
    })
    {
        dependent.WaitForCompletion(migrations);
    }

    // Sweep retention every few seconds in the integration suite so the
    // round-trip test isn't waiting on the production daily timer.
    auditObservability.WithEnvironment("AuditObservability__Retention__TickInterval", "00:00:03");

    // The ingest breakdown's stamps, for the integration suite only. The type
    // default stays false (AuditMeasurementSwitchTests) because the stamps sit
    // on a write path and production does not pay for an instrument nobody
    // reads. But a run that has to remember a shell export is a run whose
    // breakdown silently reports zeros, so the fixture turns it on rather than
    // asking (spec 109 US1).
    auditObservability.WithEnvironment("AuditObservability__Measurement__RecordIngestBreakdown", "true");
}

// ADR-0106: single YARP API gateway at the edge — fronts all nine context REST
// APIs via service discovery (#1002). CORS/TLS (#1003) and rate limiting (#1004)
// follow. Realtime WebSocket (ADR-0076) and WebRTC media stay direct, off the
// gateway, so the latency budget (constitution §IV) is untouched.
var apiGateway = builder
    .AddProject<Projects.SmartSentinelEye_ApiGateway>("api-gateway")
    .WithHttpEndpoint()
    .WithExternalHttpEndpoints()
    .WithReference(cameraCatalog)
    .WithReference(streamDistribution)
    .WithReference(layoutComposition)
    .WithReference(eventIngestion)
    .WithReference(overlayDesigner)
    .WithReference(systemVariables)
    .WithReference(auditObservability)
    .WithReference(automation)
    .WithReference(identity);

// HA (#1005): run >= 2 gateway replicas so the single REST front door is not a
// single point of failure (ADR-0106). Kept to one instance under E2E tests so
// the gateway routing/rate-limit integration tests resolve a single endpoint.
if (!isE2ETests)
{
    apiGateway.WithReplicas(2);
}

// React apps per ADR-0074: two pnpm-workspace apps under apps/. Skipped in
// test mode so the integration suite doesn't start two Node dev servers.
// Endpoints are proxyless (isProxied: false): the Vite dev server binds the
// fixed PORT (5173/5174) directly, so DCP must NOT also proxy that port — a
// shared port makes Vite fail to start with "Port is already in use".
if (isRunMode && !isE2ETests)
{
    // REST goes through the gateway (#1005): each app gets VITE_API_GATEWAY_URL
    // and calls `${gateway}/<context>/...` cross-origin (the gateway's CORS
    // policy allows the app origins, #1003). Auth is direct to Keycloak (OIDC):
    // VITE_KEYCLOAK_URL is the same Aspire endpoint the services validate tokens
    // against, so the issuer matches (ServiceDefaults.AddBearerAuthentication).
    // The realtime WebSocket hub (ADR-0076, LayoutComposition) and WebRTC media
    // stay direct — a direct reference to layout-composition keeps that URL
    // resolvable, off the gateway.
    //
    // VITE_LAYOUT_HUB_ORIGIN is the shell-safe alias the SPAs actually use to
    // build their `/hubs` dev proxy. Aspire's own service-discovery key is
    // `services__layout-composition__http__0`, and that name contains hyphens —
    // POSIX shells (bash, dash) refuse to import environment variables whose
    // names aren't valid identifiers, so `npm run dev` (which spawns Vite via
    // `sh -c`) drops it on Linux. cmd.exe keeps it, so this reproduces only off
    // Windows: the proxy is silently omitted, every `/hubs/layouts/negotiate`
    // 404s, and the kiosk sits on a permanently-stuck "live updates degraded"
    // badge (spec 011 FR-006/FR-010).
    //
    // WaitFor(layoutComposition) is ordering hygiene — vite.config.ts reads the
    // environment once, at config-evaluation time, so the endpoint should be
    // resolvable before the app starts.
    builder.AddNpmApp("management-web", "../../apps/management-web", "dev")
        .WithHttpEndpoint(env: "PORT", port: 5173, isProxied: false)
        .WithReference(apiGateway)
        .WithEnvironment("VITE_API_GATEWAY_URL", apiGateway.GetEndpoint("http"))
        .WithReference(keycloak)
        .WithEnvironment("VITE_KEYCLOAK_URL", keycloak.GetEndpoint("http"))
        .WithReference(layoutComposition)
        .WithEnvironment("VITE_LAYOUT_HUB_ORIGIN", layoutComposition.GetEndpoint("http"))
        .WaitFor(layoutComposition)
        .WithExternalHttpEndpoints()
        // Dashboard grouping: nest the SPAs under the gateway they call.
        .WithParentRelationship(apiGateway);

    builder.AddNpmApp("kiosk-web", "../../apps/kiosk-web", "dev")
        .WithHttpEndpoint(env: "PORT", port: 5174, isProxied: false)
        .WithReference(apiGateway)
        .WithEnvironment("VITE_API_GATEWAY_URL", apiGateway.GetEndpoint("http"))
        .WithReference(keycloak)
        .WithEnvironment("VITE_KEYCLOAK_URL", keycloak.GetEndpoint("http"))
        .WithReference(layoutComposition)
        .WithEnvironment("VITE_LAYOUT_HUB_ORIGIN", layoutComposition.GetEndpoint("http"))
        .WaitFor(layoutComposition)
        .WithExternalHttpEndpoints()
        .WithParentRelationship(apiGateway);

    // **The same application, deployed as a wall display** (spec 052).
    //
    // A second instance rather than a flag on the first, because a fab runs
    // both: screens somebody stands at, and screens bolted to a wall. Only this
    // one signs in as `kiosk-wall` and asks for a grant that outlives the
    // session ceiling — and only this one is narrowed to read-only, since
    // scopes belong to clients.
    //
    // It exists in the dev stack so the difference is exercised rather than
    // described. Without it, wall mode would be a code path nothing runs.
    builder.AddNpmApp("kiosk-wall", "../../apps/kiosk-web", "dev")
        .WithHttpEndpoint(env: "PORT", port: 5175, isProxied: false)
        .WithReference(apiGateway)
        .WithEnvironment("VITE_API_GATEWAY_URL", apiGateway.GetEndpoint("http"))
        .WithReference(keycloak)
        .WithEnvironment("VITE_KEYCLOAK_URL", keycloak.GetEndpoint("http"))
        .WithReference(layoutComposition)
        .WithEnvironment("VITE_LAYOUT_HUB_ORIGIN", layoutComposition.GetEndpoint("http"))
        .WithEnvironment("VITE_KIOSK_MODE", "wall")
        .WaitFor(layoutComposition)
        .WithExternalHttpEndpoints()
        .WithParentRelationship(apiGateway);
}

// Scenario Simulator (ADR-0111 M1) — dev-only, and it takes three conjuncts to
// say that, because no two of them exclude the same thing (#2013):
//
// - `isRunMode` — absent from publish mode, so it never reaches prod output.
// - `!isE2ETests` — absent from the integration fixture, which drives no
//   browser and would pay for a container and a bind mount nothing there
//   reads. No FFmpeg is wasted: camera-sim's paths are `runOnDemand`, so one
//   starts only when a reader connects, and no reader ever does.
// - `isScenarioSimulatorEnabled` — absent where `ci.yml` passes
//   `ScenarioSimulator=false`, which is the end-to-end job. That job boots a
//   *run-mode* stack, so neither of the other two conjuncts reached it, and the
//   simulator seeded twelve cameras (3 scenarios x 4 assets) and three walls
//   into the catalogue the Playwright specs share — so the kiosk picker's first
//   entry and every camera dropdown carried data no spec put there.
//
// The main `mediamtx.yml` stays clean in all three cases.
//
// - camera-sim: a second, config-clean MediaMTX. Holds NO static paths; the
//   worker provisions a `runOnDemand` loop path per catalog camera via the HTTP
//   API (9997). The loop clip is bind-mounted at /media.
// - scenario-simulator worker: seeds the camera catalog over HTTP
//   (client_credentials via Keycloak), then — driven by CameraRegisteredV1 over
//   RabbitMQ — provisions the looping video on camera-sim. Waits for the things
//   it calls on startup.
if (isRunMode && !isE2ETests && isScenarioSimulatorEnabled)
{
    var cameraSim = builder
        .AddContainer("camera-sim", "bluenviron/mediamtx", "1.21.0-ffmpeg")
        .WithBindMount("Resources/camera-sim.yml", "/mediamtx.yml")
        // The whole clips directory, not one file: an asset names its own clip
        // (spec 044) and mounting them individually would mean editing AppHost
        // every time a scenario gains a station. sim-loop.mp4 lives in here too
        // and stays the default for any camera with no scenario asset.
        .WithBindMount("Resources/clips", "/media")
        .WithHttpEndpoint(targetPort: 9997, name: "api")
        .WithEndpoint(targetPort: 8554, name: "rtsp", scheme: "tcp")
        .WithLifetime(ContainerLifetime.Persistent);

    var scenarioSimulator = builder
        .AddProject<Projects.SmartSentinelEye_ScenarioSimulator>("scenario-simulator")
        .WithReference(cameraCatalog)
        .WithReference(cameraSim.GetEndpoint("api"))
        .WithReference(overlayDesigner)
        .WithReference(automation)
        .WithReference(layoutComposition)
        .WithReference(mosquitto.GetEndpoint("mqtt"))
        .WithReference(rabbitmq)
        .WithReference(keycloak)
        .WithEnvironment("ScenarioSimulator__Runtime__ClientSecret", scenarioSimulatorClientSecret)
        // FR-007. The worker validates that an asset's clip exists before
        // provisioning a path for it, and it cannot see camera-sim's bind mount —
        // so the side that owns the mount tells it where the clips are.
        .WithEnvironment("ScenarioSimulator__ClipsDirectory", Path.GetFullPath("Resources/clips"))
        .WaitFor(cameraCatalog)
        .WaitFor(cameraSim)
        .WaitFor(overlayDesigner)
        .WaitFor(automation)
        .WaitFor(layoutComposition)
        .WaitFor(mosquitto)
        .WaitFor(rabbitmq)
        .WaitFor(keycloak);

    // Dashboard grouping: nest the dev-only camera-sim MediaMTX under the
    // simulator worker that provisions + drives its loop paths.
    cameraSim.WithParentRelationship(scenarioSimulator);
}

await builder.Build().RunAsync();
