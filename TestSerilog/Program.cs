using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

// Enable Serilog self-logging to see internal errors
Serilog.Debugging.SelfLog.Enable(msg => Console.WriteLine($"[Serilog Internal Error] {msg}"));

// อ่านค่า Configuration ก่อน
var grafanaEndpoint = Environment.GetEnvironmentVariable("Grafana__OtlpEndpoint");
var grafanaToken = Environment.GetEnvironmentVariable("Grafana__ApiToken");
var instanceId = Environment.GetEnvironmentVariable("Grafana__InstanceId") ?? "1391357";

// แสดงข้อมูล Configuration
Console.WriteLine("╔════════════════════════════════════════╗");
Console.WriteLine("║   Grafana Configuration Check          ║");
Console.WriteLine("╚════════════════════════════════════════╝");
Console.WriteLine($"📍 Endpoint: {grafanaEndpoint ?? "❌ NOT SET"}");
Console.WriteLine($"🔑 Token: {(string.IsNullOrEmpty(grafanaToken) ? "❌ NOT SET" : $"✅ SET (length: {grafanaToken.Length})")}");
Console.WriteLine($"🆔 Instance ID: {instanceId}");
Console.WriteLine($"🔐 Token starts with 'glc_': {grafanaToken?.StartsWith("glc_") ?? false}");
Console.WriteLine();

// ตั้งค่า Environment Variables สำหรับ OpenTelemetry
if (!string.IsNullOrEmpty(grafanaEndpoint) && !string.IsNullOrEmpty(grafanaToken))
{
    string authHeaderValue;
    if (grafanaToken.StartsWith("glc_"))
    {
        var credentials = $"{instanceId}:{grafanaToken}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(credentials);
        authHeaderValue = Convert.ToBase64String(bytes);
        Console.WriteLine("🔧 Using glc_ token format with Instance ID");
    }
    else
    {
        authHeaderValue = grafanaToken;
        Console.WriteLine("🔧 Using pre-encoded token");
    }

    // ตั้งค่า Environment Variables
    Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", grafanaEndpoint);
    Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", $"Authorization=Basic {authHeaderValue}");
    Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

    Console.WriteLine($"✅ OTEL_EXPORTER_OTLP_ENDPOINT set to: {grafanaEndpoint}");
    Console.WriteLine($"✅ OTEL_EXPORTER_OTLP_HEADERS configured");
    Console.WriteLine($"✅ Auth Header (first 20 chars): {authHeaderValue.Substring(0, Math.Min(20, authHeaderValue.Length))}...");
    Console.WriteLine();
}
else
{
    Console.WriteLine("❌ Missing Grafana configuration - Logs will only appear in Console");
    Console.WriteLine();
}

// สร้าง Serilog Logger
Serilog.Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.OpenTelemetry(opts =>
    {
        opts.Endpoint = grafanaEndpoint;
        opts.Protocol = OtlpProtocol.HttpProtobuf;
    })
    .CreateLogger();

Console.WriteLine("✅ Serilog Logger created with Console and OpenTelemetry sinks");
Console.WriteLine();

// ส่ง Test Logs ทันที
Console.WriteLine("🧪 Sending test logs...");
Serilog.Log.Information("🚀 TEST LOG #1 - Application starting at {Timestamp}", DateTime.UtcNow);
Serilog.Log.Warning("⚠️ TEST LOG #2 - This is a warning message");
Serilog.Log.Error("❌ TEST LOG #3 - This is an error message");
Console.WriteLine("✅ Test logs sent to both Console and OpenTelemetry");
Console.WriteLine();

var builder = WebApplication.CreateBuilder(args);

// Use Serilog
builder.Host.UseSerilog();

// Fallback: อ่านจาก appsettings.json ถ้าไม่มีใน Environment Variables
grafanaEndpoint = grafanaEndpoint ?? builder.Configuration["Grafana:OtlpEndpoint"];
grafanaToken = grafanaToken ?? builder.Configuration["Grafana:ApiToken"];

// Configure OpenTelemetry with proper exporters
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(
        serviceName: "my-app",
        serviceNamespace: "my-application-group",
        serviceVersion: "1.0.0")
    .AddAttributes(new Dictionary<string, object>
    {
        ["deployment.environment"] = "production"
    });

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metric => metric
        .AddHttpClientInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
});

// Add services to the container.
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

app.MapRazorPages();
app.MapGet("/", () => Results.Redirect("/Index"));

// Test log endpoint
app.MapGet("/log", () =>
{
    Console.WriteLine("🔔 /log endpoint called");
    Serilog.Log.Information("📝 Test log from /log endpoint - hit at {Time}", DateTime.UtcNow);
    Serilog.Log.Warning("⚠️ Warning message from /log endpoint");
    Serilog.Log.Error("❌ Error message from /log endpoint");

    return Results.Ok(new
    {
        message = "Logs written successfully!",
        timestamp = DateTime.UtcNow,
        grafanaConfigured = !string.IsNullOrEmpty(grafanaEndpoint),
        instructions = "Check Grafana Explore → Loki → Query: {service_name=\"my-app\"} in the last 5 minutes"
    });
});

// Test telemetry endpoint
app.MapGet("/test-telemetry", () =>
{
    Console.WriteLine("🔔 /test-telemetry endpoint called");
    var activitySource = new System.Diagnostics.ActivitySource("my-app");

    using (var activity = activitySource.StartActivity("TestOperation"))
    {
        activity?.SetTag("test.type", "manual");
        activity?.SetTag("test.timestamp", DateTime.UtcNow.ToString("o"));
        activity?.AddEvent(new System.Diagnostics.ActivityEvent("Test event from my-app"));

        Serilog.Log.Information("🔬 Test telemetry sent to Grafana at {Time}", DateTime.UtcNow);

        // Simulate some work
        Thread.Sleep(100);

        return Results.Ok(new
        {
            message = "Telemetry test sent!",
            traceId = activity?.TraceId.ToString(),
            spanId = activity?.SpanId.ToString(),
            serviceName = "my-app",
            timestamp = DateTime.UtcNow,
            instructions = "Check Grafana Explore → Tempo (for traces) or Loki (for logs)"
        });
    }
});

// Connection test endpoint
app.MapGet("/test-connection", async () =>
{
    Console.WriteLine("🔔 /test-connection endpoint called");

    var results = new
    {
        timestamp = DateTime.UtcNow,
        configuration = new
        {
            endpointConfigured = !string.IsNullOrEmpty(grafanaEndpoint),
            endpoint = grafanaEndpoint ?? "NOT SET",
            tokenConfigured = !string.IsNullOrEmpty(grafanaToken),
            tokenLength = grafanaToken?.Length ?? 0,
            instanceId = instanceId
        },
        testLogs = new
        {
            sent = true,
            count = 3,
            message = "Check Grafana in 30-60 seconds"
        },
        grafanaInstructions = new
        {
            step1 = "Go to Grafana Cloud → Explore",
            step2 = "Select Data Source: Loki",
            step3 = "Query: {service_name=\"my-app\"}",
            step4 = "Time range: Last 5 minutes"
        }
    };

    // ส่ง test logs
    Serilog.Log.Information("🧪 Connection test initiated at {Time}", DateTime.UtcNow);
    Serilog.Log.Warning("⚠️ Connection test warning");
    Serilog.Log.Error("❌ Connection test error");

    // รอให้ส่งเสร็จ
    await Task.Delay(1000);

    return Results.Ok(results);
});

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
    grafanaConfigured = !string.IsNullOrEmpty(grafanaEndpoint)
}));

try
{
    Console.WriteLine("╔════════════════════════════════════════╗");
    Console.WriteLine("║   Application Starting                 ║");
    Console.WriteLine("╚════════════════════════════════════════╝");

    Serilog.Log.Information("🚀 Starting web application with Grafana OpenTelemetry");
    Serilog.Log.Information("📦 Service Name: my-app");
    Serilog.Log.Information("🏢 Service Namespace: my-application-group");

    Console.WriteLine();
    Console.WriteLine("📋 Available Test Endpoints:");
    Console.WriteLine("   • GET /log - Send test logs");
    Console.WriteLine("   • GET /test-telemetry - Send test traces");
    Console.WriteLine("   • GET /test-connection - Full connection test");
    Console.WriteLine("   • GET /health - Health check");
    Console.WriteLine();
    Console.WriteLine("🔍 To verify Grafana connection:");
    Console.WriteLine("   1. Call any endpoint above");
    Console.WriteLine("   2. Wait 30-60 seconds");
    Console.WriteLine("   3. Go to Grafana Cloud → Explore → Loki");
    Console.WriteLine("   4. Query: {service_name=\"my-app\"}");
    Console.WriteLine("   5. Set time range to 'Last 5 minutes'");
    Console.WriteLine();
    Console.WriteLine("✅ Application is ready!");
    Console.WriteLine("════════════════════════════════════════");
    Console.WriteLine();

    app.Run();
}
catch (Exception ex)
{
    Serilog.Log.Fatal(ex, "💥 Application terminated unexpectedly");
    Console.WriteLine($"❌ Fatal Error: {ex.Message}");
}
finally
{
    Console.WriteLine("🛑 Shutting down - flushing logs...");
    await Serilog.Log.CloseAndFlushAsync();
    Console.WriteLine("✅ Logs flushed successfully");
}