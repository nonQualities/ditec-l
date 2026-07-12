using DITEC.Attendance.Data;
using Nelknet.LibSQL.Data;
using QRCoder;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Render assigns the port to listen on via $PORT and expects the app to bind
// 0.0.0.0:$PORT. Locally, Kestrel:Endpoints:Http:Url from appsettings.json
// (or launch settings) is used instead.
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}

var app = builder.Build();

// ---------- Configuration ----------
var inCutoff = TimeOnly.Parse(builder.Configuration["Attendance:InCutoffTime"] ?? "11:00:00");
var outStart = TimeOnly.Parse(builder.Configuration["Attendance:OutStartTime"] ?? "17:00:00");
var faceThreshold = double.Parse(builder.Configuration["Attendance:FaceMatchThreshold"] ?? "0.55");

// ---------- Database connection ----------
// TURSO_DATABASE_URL / TURSO_AUTH_TOKEN (set as Render env vars) point at a
// remote Turso database — used in production, since Render's local disk is
// ephemeral and a local ditec.db file would be wiped on every deploy/restart.
// When they're absent (local development), fall back to a local .db file,
// which Nelknet.LibSQL.Data also happens to support with the same API.
var tursoUrl = Environment.GetEnvironmentVariable("TURSO_DATABASE_URL")
    ?? builder.Configuration["Turso:DatabaseUrl"];
var tursoToken = Environment.GetEnvironmentVariable("TURSO_AUTH_TOKEN")
    ?? builder.Configuration["Turso:AuthToken"];

string connectionString;
if (!string.IsNullOrWhiteSpace(tursoUrl))
{
    connectionString = new LibSQLConnectionStringBuilder
    {
        DataSource = tursoUrl,
        AuthToken = tursoToken ?? ""
    }.ConnectionString;
}
else
{
    var dbPath = builder.Configuration["Attendance:DatabasePath"] ?? "ditec.db";
    connectionString = new LibSQLConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
}

var db = new Db(connectionString);
db.Initialize();

app.UseDefaultFiles();
app.UseStaticFiles();

// ================= Employees / Admin =================

app.MapGet("/api/employees", () => Results.Ok(db.GetAllEmployees()));

app.MapPost("/api/employees", (CreateEmployeeRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.EmployeeCode) || string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { message = "Employee code and name are required." });

    try
    {
        var emp = db.CreateEmployee(req.EmployeeCode.Trim(), req.Name.Trim(), req.Department?.Trim() ?? "");
        return Results.Ok(emp);
    }
    catch (LibSQLConstraintException)
    {
        return Results.Conflict(new { message = "An employee with that code already exists." });
    }
});

app.MapDelete("/api/employees/{id:int}", (int id) =>
{
    db.DeleteEmployee(id);
    return Results.NoContent();
});

// Server-rendered QR PNG for a given employee's badge.
app.MapGet("/api/employees/{id:int}/qr", (int id) =>
{
    var emp = db.GetEmployeeById(id);
    if (emp is null) return Results.NotFound();

    using var generator = new QRCodeGenerator();
    using var data = generator.CreateQrCode(emp.QrToken, QRCodeGenerator.ECCLevel.Q);
    var pngQr = new PngByteQRCode(data);
    var bytes = pngQr.GetGraphic(10);
    return Results.File(bytes, "image/png");
});

app.MapPost("/api/employees/{id:int}/face", (int id, FaceEnrollRequest req) =>
{
    var emp = db.GetEmployeeById(id);
    if (emp is null) return Results.NotFound();
    if (req.Descriptor is null || req.Descriptor.Length == 0)
        return Results.BadRequest(new { message = "No face descriptor supplied." });

    db.SaveFaceDescriptor(id, JsonSerializer.Serialize(req.Descriptor));
    return Results.Ok(new { message = "Face enrolled." });
});

// ================= Kiosk: lookup + verify =================

app.MapGet("/api/attendance/lookup/{qrToken}", (string qrToken) =>
{
    var emp = db.GetEmployeeByToken(qrToken);
    if (emp is null)
        return Results.Ok(new LookupResult { Found = false, Message = "QR code not recognized." });

    var today = DateOnly.FromDateTime(DateTime.Now);
    var logs = db.GetLogsForDate(emp.Id, today);

    return Results.Ok(new LookupResult
    {
        Found = true,
        Employee = emp,
        TodayLogs = logs
    });
});

app.MapPost("/api/attendance/verify", (VerifyRequest req) =>
{
    var emp = db.GetEmployeeByToken(req.QrToken);
    if (emp is null)
        return Results.Ok(new VerifyResult { Success = false, Message = "QR code not recognized." });

    var storedJson = db.GetFaceDescriptorJson(emp.Id);
    if (storedJson is null)
        return Results.Ok(new VerifyResult { Success = false, Message = $"No face on file for {emp.Name}. Ask an admin to enroll their face first." });

    var stored = JsonSerializer.Deserialize<float[]>(storedJson)!;
    if (req.Descriptor.Length != stored.Length)
        return Results.Ok(new VerifyResult { Success = false, Message = "Face data invalid — try again." });

    var distance = EuclideanDistance(stored, req.Descriptor);
    if (distance > faceThreshold)
        return Results.Ok(new VerifyResult { Success = false, Message = "Face does not match this ID.", Distance = distance });

    var now = DateTime.Now;
    var today = DateOnly.FromDateTime(now);
    var nowTime = TimeOnly.FromDateTime(now);

    if (nowTime <= inCutoff)
    {
        if (db.HasInToday(emp.Id, today))
            return Results.Ok(new VerifyResult { Success = false, Message = $"{emp.Name} already has an In-time recorded today.", Distance = distance });

        var log = db.InsertLog(emp.Id, "In", now.ToUniversalTime());
        return Results.Ok(new VerifyResult { Success = true, Message = $"Welcome, {emp.Name}. In-time recorded.", LogType = "In", Timestamp = log.Timestamp, Distance = distance });
    }

    if (nowTime >= outStart)
    {
        var log = db.InsertLog(emp.Id, "Out", now.ToUniversalTime());
        return Results.Ok(new VerifyResult { Success = true, Message = $"Goodbye, {emp.Name}. Out-time recorded.", LogType = "Out", Timestamp = log.Timestamp, Distance = distance });
    }

    return Results.Ok(new VerifyResult
    {
        Success = false,
        Message = $"Outside the attendance window (In before {inCutoff:h:mm tt}, Out after {outStart:h:mm tt}).",
        Distance = distance
    });
});

app.MapGet("/api/attendance/report", (string? date) =>
{
    var d = string.IsNullOrWhiteSpace(date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(date);
    var rows = db.GetReportForDate(d)
        .Select(r => new
        {
            employeeCode = r.Employee.EmployeeCode,
            name = r.Employee.Name,
            department = r.Employee.Department,
            logType = r.Log.LogType,
            timestamp = r.Log.Timestamp
        });
    return Results.Ok(rows);
});

app.Run();

static double EuclideanDistance(float[] a, float[] b)
{
    double sum = 0;
    for (int i = 0; i < a.Length; i++)
    {
        var d = a[i] - b[i];
        sum += d * d;
    }
    return Math.Sqrt(sum);
}

record CreateEmployeeRequest(string EmployeeCode, string Name, string? Department);
record FaceEnrollRequest(float[] Descriptor);
