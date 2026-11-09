using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FleetApi.Data;
using FleetApi.Models;
using FleetApi.Security;

// 配置从程序集目录读，而不是进程当前工作目录：
// 用 `dotnet /abs/path/FleetApi.dll` 从任意目录启动时也必须能找到 appsettings.json。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddEnvironmentVariables(prefix: "FLEET_");

var secret = builder.Configuration["JwtSecret"]
    ?? throw new InvalidOperationException("JwtSecret 未配置");
var adminUser = builder.Configuration["AdminUser"] ?? "admin";
var adminPassword = builder.Configuration["AdminPassword"] ?? "admin123";
var rateLimit = int.TryParse(builder.Configuration["OrderRateLimit"], out var rl) ? rl : 20;
var dedupeSeconds = int.TryParse(builder.Configuration["OrderDedupeSeconds"], out var ds) ? ds : 600;

// 换皮项目刻意不用 EF Core：内存仓储让「同一套 API 在不同语言里怎么写」
// 的对照不被 ORM 差异淹没，也让 dotnet run 无需任何数据库或 NuGet 还原。
builder.Services.AddSingleton(new ServiceStore());
builder.Services.AddSingleton(new OrderStore(rateLimit, dedupeSeconds));

var app = builder.Build();

// ---- 统一异常 -> HTTP 状态码。业务层只抛 RejectedException，不碰 HTTP ----
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (RejectedException ex)
    {
        ctx.Response.StatusCode = ex.Kind switch
        {
            "rate-limited" => 429,
            "duplicate" => 409,
            "not-found" => 404,
            "bad-transition" or "bad-status" => 409,
            _ => 400,
        };
        await ctx.Response.WriteAsJsonAsync(new ErrorDto(ex.Message));
    }
});

// ---- CORS + JWT 中间件（顺序对应 Spring 的 CorsConfig + JwtAuth）--------
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Headers.AccessControlAllowOrigin = origin;
        ctx.Response.Headers.AccessControlAllowMethods = "GET,POST,PATCH,DELETE,OPTIONS";
        ctx.Response.Headers.AccessControlAllowHeaders = "Authorization,Content-Type";
        ctx.Response.Headers.AccessControlMaxAge = "3600";
    }
    // 预检不带 Authorization 是浏览器规范行为，拦了跨源前端就永远连不上
    if (HttpMethods.IsOptions(ctx.Request.Method))
    {
        ctx.Response.StatusCode = 204;
        return;
    }
    var path = ctx.Request.Path.Value ?? "";
    if (path == "/api/login" || path == "/actuator/health")
    {
        await next();
        return;
    }
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) ||
        !JwtService.Verify(auth[7..], secret))
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new ErrorDto("缺少或无效的 Bearer token"));
        return;
    }
    await next();
});

static string ClientKey(HttpContext ctx)
{
    var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(forwarded)) return forwarded.Split(',')[0].Trim();
    return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// ---- 登录 ---------------------------------------------------------------
app.MapPost("/api/login", (JsonElement body) =>
{
    var user = ServiceStore.Str(body, "username", 64);
    var pass = ServiceStore.Str(body, "password", 128);
    var uOk = FixedEqual(user, adminUser);
    var pOk = FixedEqual(pass, adminPassword);
    if (!uOk || !pOk)
        return Results.Json(new ErrorDto("用户名或密码错误"), statusCode: 401);
    return Results.Json(new { token = JwtService.Issue(user, secret) });
});

// ---- 服务表 -------------------------------------------------------------
app.MapGet("/api/services", (ServiceStore store) => Results.Json(store.List()));

app.MapGet("/api/services/{id:long}", (long id, ServiceStore store) =>
    store.Get(id) is { } row ? Results.Json(row) : Results.NotFound());

app.MapPost("/api/services", (JsonElement body, ServiceStore store) =>
{
    var (row, error) = store.Create(body);
    return error is null
        ? Results.Json(row, statusCode: 201)
        : Results.Json(new ErrorDto(error), statusCode: 409);
});

app.MapPatch("/api/services/{id:long}", (long id, JsonElement patch, ServiceStore store) =>
{
    if (!store.Patch(id, patch)) return Results.NotFound();
    return Results.Json(store.Get(id));
});

app.MapDelete("/api/services/{id:long}", (long id, ServiceStore store) =>
    store.Delete(id) ? Results.Json(new { deleted = id }) : Results.NotFound());

app.MapPost("/api/report", (JsonElement body, ServiceStore store) =>
{
    var reporter = ServiceStore.Str(body, "reporter", 255);
    if (string.IsNullOrWhiteSpace(reporter)) reporter = "unknown";
    if (!body.TryGetProperty("services", out var services))
        return Results.Json(new ErrorDto("services 必须是数组"), statusCode: 400);
    var accepted = store.Replace(reporter, services);
    return accepted < 0
        ? Results.Json(new ErrorDto("services 必须是数组"), statusCode: 400)
        : Results.Json(new { reporter, accepted });
});

// ---- 订单 ---------------------------------------------------------------
app.MapPost("/api/orders", (JsonElement body, HttpContext ctx, OrderStore store) =>
    Results.Json(store.Create(body, ClientKey(ctx)), statusCode: 201));

app.MapGet("/api/orders", (string? status, string? source, OrderStore store) =>
    Results.Json(store.List(status, source)));

app.MapGet("/api/orders/stats", (OrderStore store) => Results.Json(store.Stats()));

app.MapPatch("/api/orders/{id:long}/status", (long id, JsonElement body, OrderStore store) =>
    Results.Json(store.ChangeStatus(id, ServiceStore.Str(body, "status", 16))));

app.MapGet("/actuator/health", () => Results.Json(new { status = "UP" }));

app.Run();

// 固定时长比较：避免按字符短路造成的时序侧信道
static bool FixedEqual(string a, string b)
{
    var x = Encoding.UTF8.GetBytes(a);
    var y = Encoding.UTF8.GetBytes(b);
    return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
}
