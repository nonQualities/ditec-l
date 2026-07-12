using Nelknet.LibSQL.Data;

namespace DITEC.Attendance.Data;

/// <summary>
/// Deliberately avoids EF Core: this is a handful of narrow queries against a
/// SQLite-compatible database, so plain ADO.NET keeps the app's footprint small
/// (no change-tracking, no migrations pipeline, near-instant cold start).
///
/// Uses Nelknet.LibSQL.Data instead of Microsoft.Data.Sqlite so the exact same
/// code path can target either a local .db file (local dev) or a remote Turso
/// database over HTTPS (production/Render) — only the connection string differs.
/// See Program.cs for how that connection string is chosen.
/// </summary>
public class Db
{
    private readonly string _connString;

    public Db(string connectionString)
    {
        _connString = connectionString;
    }

    private LibSQLConnection Open()
    {
        var conn = new LibSQLConnection(_connString);
        conn.Open();
        try
        {
            // journal_mode=WAL only makes sense for a local file, and is a no-op/
            // error on a remote libSQL connection, so it's intentionally skipped.
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        catch
        {
            // Some remote configurations don't support session-level PRAGMAs;
            // foreign key enforcement is a nice-to-have here, not load-bearing.
        }
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Employees (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EmployeeCode TEXT UNIQUE NOT NULL,
                Name TEXT NOT NULL,
                Department TEXT NOT NULL DEFAULT '',
                QrToken TEXT UNIQUE NOT NULL,
                FaceDescriptor TEXT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AttendanceLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EmployeeId INTEGER NOT NULL,
                LogType TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                FOREIGN KEY(EmployeeId) REFERENCES Employees(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_Logs_Employee_Time ON AttendanceLogs(EmployeeId, Timestamp);
            """;
        cmd.ExecuteNonQuery();
    }

    // ---------- Employees ----------

    public Employee CreateEmployee(string code, string name, string department)
    {
        var token = Guid.NewGuid().ToString("N");
        var createdAt = DateTime.UtcNow.ToString("O");

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Employees (EmployeeCode, Name, Department, QrToken, CreatedAt)
            VALUES ($code, $name, $dept, $token, $createdAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$dept", department ?? "");
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$createdAt", createdAt);

        var id = Convert.ToInt32((long)cmd.ExecuteScalar()!);
        return new Employee
        {
            Id = id,
            EmployeeCode = code,
            Name = name,
            Department = department ?? "",
            QrToken = token,
            FaceEnrolled = false,
            CreatedAt = createdAt
        };
    }

    public List<Employee> GetAllEmployees()
    {
        var list = new List<Employee>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, EmployeeCode, Name, Department, QrToken, FaceDescriptor, CreatedAt
            FROM Employees ORDER BY Id DESC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadEmployee(reader));
        return list;
    }

    public Employee? GetEmployeeById(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, EmployeeCode, Name, Department, QrToken, FaceDescriptor, CreatedAt
            FROM Employees WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEmployee(reader) : null;
    }

    public Employee? GetEmployeeByToken(string token)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, EmployeeCode, Name, Department, QrToken, FaceDescriptor, CreatedAt
            FROM Employees WHERE QrToken = $token;
            """;
        cmd.Parameters.AddWithValue("$token", token);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEmployee(reader) : null;
    }

    public void DeleteEmployee(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Employees WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public string? GetFaceDescriptorJson(int employeeId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FaceDescriptor FROM Employees WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", employeeId);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : (string)result;
    }

    public void SaveFaceDescriptor(int employeeId, string descriptorJson)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Employees SET FaceDescriptor = $desc WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$desc", descriptorJson);
        cmd.Parameters.AddWithValue("$id", employeeId);
        cmd.ExecuteNonQuery();
    }

    private static Employee ReadEmployee(LibSQLDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        EmployeeCode = reader.GetString(1),
        Name = reader.GetString(2),
        Department = reader.IsDBNull(3) ? "" : reader.GetString(3),
        QrToken = reader.GetString(4),
        FaceEnrolled = !reader.IsDBNull(5),
        CreatedAt = reader.GetString(6)
    };

    // ---------- Attendance ----------

    public List<AttendanceLog> GetLogsForDate(int employeeId, DateOnly date)
    {
        var (start, end) = DayBounds(date);
        var list = new List<AttendanceLog>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, EmployeeId, LogType, Timestamp FROM AttendanceLogs
            WHERE EmployeeId = $id AND Timestamp >= $start AND Timestamp < $end
            ORDER BY Timestamp ASC;
            """;
        cmd.Parameters.AddWithValue("$id", employeeId);
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$end", end);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AttendanceLog
            {
                Id = reader.GetInt32(0),
                EmployeeId = reader.GetInt32(1),
                LogType = reader.GetString(2),
                Timestamp = reader.GetString(3)
            });
        }
        return list;
    }

    public bool HasInToday(int employeeId, DateOnly date) =>
        GetLogsForDate(employeeId, date).Any(l => l.LogType == "In");

    public AttendanceLog InsertLog(int employeeId, string logType, DateTime timestampUtc)
    {
        var ts = timestampUtc.ToString("O");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AttendanceLogs (EmployeeId, LogType, Timestamp)
            VALUES ($eid, $type, $ts);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$eid", employeeId);
        cmd.Parameters.AddWithValue("$type", logType);
        cmd.Parameters.AddWithValue("$ts", ts);
        var id = Convert.ToInt32((long)cmd.ExecuteScalar()!);
        return new AttendanceLog { Id = id, EmployeeId = employeeId, LogType = logType, Timestamp = ts };
    }

    public List<(Employee Employee, AttendanceLog Log)> GetReportForDate(DateOnly date)
    {
        var (start, end) = DayBounds(date);
        var list = new List<(Employee, AttendanceLog)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.EmployeeCode, e.Name, e.Department, e.QrToken, e.FaceDescriptor, e.CreatedAt,
                   l.Id, l.EmployeeId, l.LogType, l.Timestamp
            FROM AttendanceLogs l
            JOIN Employees e ON e.Id = l.EmployeeId
            WHERE l.Timestamp >= $start AND l.Timestamp < $end
            ORDER BY l.Timestamp ASC;
            """;
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$end", end);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var emp = ReadEmployee(reader);
            var log = new AttendanceLog
            {
                Id = reader.GetInt32(7),
                EmployeeId = reader.GetInt32(8),
                LogType = reader.GetString(9),
                Timestamp = reader.GetString(10)
            };
            list.Add((emp, log));
        }
        return list;
    }

    private static (string start, string end) DayBounds(DateOnly date)
    {
        var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(1);
        return (start.ToString("O"), end.ToString("O"));
    }
}
