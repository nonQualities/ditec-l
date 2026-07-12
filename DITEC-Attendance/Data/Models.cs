namespace DITEC.Attendance.Data;

public class Employee
{
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";
    public string QrToken { get; set; } = "";
    public bool FaceEnrolled { get; set; }
    public string CreatedAt { get; set; } = "";
}

public class AttendanceLog
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public string LogType { get; set; } = ""; // "In" or "Out"
    public string Timestamp { get; set; } = "";
}

public class LookupResult
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public Employee? Employee { get; set; }
    public List<AttendanceLog> TodayLogs { get; set; } = new();
}

public class VerifyRequest
{
    public string QrToken { get; set; } = "";
    public float[] Descriptor { get; set; } = Array.Empty<float>();
}

public class VerifyResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? LogType { get; set; }
    public string? Timestamp { get; set; }
    public double? Distance { get; set; }
}
