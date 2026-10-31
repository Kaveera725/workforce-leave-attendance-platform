using EmployeeManagement.API.Data;
using EmployeeManagement.API.DTOs.Common;
using EmployeeManagement.API.Exceptions;
using EmployeeManagement.API.Models;
using EmployeeManagement.API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmployeeManagement.Tests;

public class AttendanceServiceTests
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AttendanceService> _logger;

    public AttendanceServiceTests()
    {
        var settings = new Dictionary<string, string?>
        {
            { "Attendance:LateAfter", "09:00" }
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<AttendanceService>();
    }

    private static ApplicationDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task CheckIn_On_WorkingDay_Creates_Attendance_PRESENT()
    {
        using var context = CreateInMemoryDbContext();
        var dept = new Department { Name = "IT" };
        context.Departments.Add(dept);

        var user = new User { Email = "john@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emp = new Employee
        {
            UserId = user.Id,
            DepartmentId = dept.Id,
            EmployeeCode = "EMP001",
            FirstName = "John",
            LastName = "Doe",
            Position = "Dev",
            Status = EmployeeStatus.ACTIVE
        };
        context.Employees.Add(emp);
        await context.SaveChangesAsync();

        var service = new AttendanceService(context, _configuration, _logger);
        // Wednesday at 08:45 AM (Working day, on time)
        var checkInTime = new DateTime(2026, 10, 14, 8, 45, 0, DateTimeKind.Utc);

        var result = await service.CheckInAsync(user.Id, checkInTime: checkInTime);

        Assert.Equal("PRESENT", result.Status);
        Assert.Equal(new DateOnly(2026, 10, 14), result.AttendanceDate);
        Assert.Equal(checkInTime, result.CheckIn);
        Assert.Null(result.CheckOut);
    }

    [Fact]
    public async Task CheckIn_After_0900_Marks_Status_LATE()
    {
        using var context = CreateInMemoryDbContext();
        var dept = new Department { Name = "IT" };
        context.Departments.Add(dept);

        var user = new User { Email = "late@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emp = new Employee
        {
            UserId = user.Id,
            DepartmentId = dept.Id,
            EmployeeCode = "EMP002",
            FirstName = "Late",
            LastName = "Worker",
            Position = "Dev",
            Status = EmployeeStatus.ACTIVE
        };
        context.Employees.Add(emp);
        await context.SaveChangesAsync();

        var service = new AttendanceService(context, _configuration, _logger);
        // Wednesday at 09:15 AM (Working day, late)
        var checkInTime = new DateTime(2026, 10, 14, 9, 15, 0, DateTimeKind.Local);

        var result = await service.CheckInAsync(user.Id, checkInTime: checkInTime);

        Assert.Equal("LATE", result.Status);
    }

    [Fact]
    public async Task CheckIn_On_Weekend_Throws_BusinessRuleException()
    {
        using var context = CreateInMemoryDbContext();
        var dept = new Department { Name = "IT" };
        context.Departments.Add(dept);

        var user = new User { Email = "weekend@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emp = new Employee { UserId = user.Id, DepartmentId = dept.Id, EmployeeCode = "EMP003", FirstName = "Weekend", LastName = "User", Position = "Dev", Status = EmployeeStatus.ACTIVE };
        context.Employees.Add(emp);
        await context.SaveChangesAsync();

        var service = new AttendanceService(context, _configuration, _logger);
        // Sunday 2026-10-18
        var sundayTime = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.CheckInAsync(user.Id, checkInTime: sundayTime));

        Assert.Contains("working days only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckIn_Duplicate_Same_Day_Throws_ConflictException_409()
    {
        using var context = CreateInMemoryDbContext();
        var dept = new Department { Name = "IT" };
        context.Departments.Add(dept);

        var user = new User { Email = "dup@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emp = new Employee { UserId = user.Id, DepartmentId = dept.Id, EmployeeCode = "EMP004", FirstName = "Dup", LastName = "User", Position = "Dev", Status = EmployeeStatus.ACTIVE };
        context.Employees.Add(emp);
        await context.SaveChangesAsync();

        var service = new AttendanceService(context, _configuration, _logger);
        var time1 = new DateTime(2026, 10, 14, 8, 30, 0, DateTimeKind.Utc);
        var time2 = new DateTime(2026, 10, 14, 8, 45, 0, DateTimeKind.Utc);

        // First check-in succeeds
        await service.CheckInAsync(user.Id, checkInTime: time1);

        // Second check-in on the same day throws ConflictException (409)
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.CheckInAsync(user.Id, checkInTime: time2));

        Assert.Contains("already checked in", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckOut_Without_CheckIn_Throws_BusinessRuleException()
    {
        using var context = CreateInMemoryDbContext();
        var dept = new Department { Name = "IT" };
        context.Departments.Add(dept);

        var user = new User { Email = "nocheckin@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emp = new Employee { UserId = user.Id, DepartmentId = dept.Id, EmployeeCode = "EMP005", FirstName = "No", LastName = "Checkin", Position = "Dev", Status = EmployeeStatus.ACTIVE };
        context.Employees.Add(emp);
        await context.SaveChangesAsync();

        var service = new AttendanceService(context, _configuration, _logger);
        var checkOutTime = new DateTime(2026, 10, 14, 17, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.CheckOutAsync(user.Id, checkOutTime: checkOutTime));
    }

    [Fact]
    public async Task CheckOut_Under_4_Hours_Worked_Marks_HALF_DAY()
    {
        using var context = CreateInMemoryDbContext();
        var dept = new Department { Name = "IT" };
        context.Departments.Add(dept);

        var user = new User { Email = "halfday@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emp = new Employee { UserId = user.Id, DepartmentId = dept.Id, EmployeeCode = "EMP006", FirstName = "Half", LastName = "Day", Position = "Dev", Status = EmployeeStatus.ACTIVE };
        context.Employees.Add(emp);
        await context.SaveChangesAsync();

        var service = new AttendanceService(context, _configuration, _logger);
        var checkInTime = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
        var checkOutTime = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc); // 3 hours

        await service.CheckInAsync(user.Id, checkInTime: checkInTime);
        var result = await service.CheckOutAsync(user.Id, checkOutTime: checkOutTime);

        Assert.Equal("HALF_DAY", result.Status);
        Assert.Equal(3.0, result.WorkedHours);
    }

    [Fact]
    public async Task GetMyTodayState_Returns_Correct_Lifecycle_States()
    {
        using var context = CreateInMemoryDbContext();
        var dept = new Department { Name = "IT" };
        context.Departments.Add(dept);

        var user = new User { Email = "state@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emp = new Employee { UserId = user.Id, DepartmentId = dept.Id, EmployeeCode = "EMP007", FirstName = "State", LastName = "Tester", Position = "Dev", Status = EmployeeStatus.ACTIVE };
        context.Employees.Add(emp);
        await context.SaveChangesAsync();

        var service = new AttendanceService(context, _configuration, _logger);

        // 1. Initially: NOT_CHECKED_IN
        var state1 = await service.GetMyTodayStateAsync(user.Id);
        Assert.Equal("NOT_CHECKED_IN", state1.State);
        Assert.True(state1.CanCheckIn);
        Assert.False(state1.CanCheckOut);

        // 2. After Check-In: CHECKED_IN
        await service.CheckInAsync(user.Id);
        var state2 = await service.GetMyTodayStateAsync(user.Id);
        Assert.Equal("CHECKED_IN", state2.State);
        Assert.False(state2.CanCheckIn);
        Assert.True(state2.CanCheckOut);

        // 3. After Check-Out: CHECKED_OUT
        await service.CheckOutAsync(user.Id);
        var state3 = await service.GetMyTodayStateAsync(user.Id);
        Assert.Equal("CHECKED_OUT", state3.State);
        Assert.False(state3.CanCheckIn);
        Assert.False(state3.CanCheckOut);
    }

    [Fact]
    public async Task GetTodaySummary_Categorizes_Present_Late_OnLeave_And_Absent_BR_13()
    {
        using var context = CreateInMemoryDbContext();
        var itDept = new Department { Name = "IT" };
        context.Departments.Add(itDept);

        // 4 Employees: 1 on time, 1 late, 1 on approved leave, 1 absent
        var u1 = new User { Email = "ontime@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        var u2 = new User { Email = "late@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        var u3 = new User { Email = "onleave@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        var u4 = new User { Email = "absent@company.com", PasswordHash = "hash", Role = UserRole.EMPLOYEE };
        context.Users.AddRange(u1, u2, u3, u4);
        await context.SaveChangesAsync();

        var e1 = new Employee { UserId = u1.Id, DepartmentId = itDept.Id, EmployeeCode = "EMP001", FirstName = "OnTime", LastName = "Emp", Position = "Dev", Status = EmployeeStatus.ACTIVE };
        var e2 = new Employee { UserId = u2.Id, DepartmentId = itDept.Id, EmployeeCode = "EMP002", FirstName = "Late", LastName = "Emp", Position = "Dev", Status = EmployeeStatus.ACTIVE };
        var e3 = new Employee { UserId = u3.Id, DepartmentId = itDept.Id, EmployeeCode = "EMP003", FirstName = "Leave", LastName = "Emp", Position = "Dev", Status = EmployeeStatus.ACTIVE };
        var e4 = new Employee { UserId = u4.Id, DepartmentId = itDept.Id, EmployeeCode = "EMP004", FirstName = "Absent", LastName = "Emp", Position = "Dev", Status = EmployeeStatus.ACTIVE };
        context.Employees.AddRange(e1, e2, e3, e4);

        var lt = new LeaveType { Name = "Annual Leave", DefaultDays = 14 };
        context.LeaveTypes.Add(lt);
        await context.SaveChangesAsync();


        var today = new DateOnly(2026, 10, 14); // Wednesday

        // e1 checked in on time
        context.Attendances.Add(new Attendance
        {
            EmployeeId = e1.Id,
            AttendanceDate = today,
            CheckIn = new DateTime(2026, 10, 14, 8, 30, 0, DateTimeKind.Utc),
            Status = AttendanceStatus.PRESENT
        });

        // e2 checked in late
        context.Attendances.Add(new Attendance
        {
            EmployeeId = e2.Id,
            AttendanceDate = today,
            CheckIn = new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Utc),
            Status = AttendanceStatus.LATE
        });

        // e3 on approved leave
        context.LeaveRequests.Add(new LeaveRequest
        {
            EmployeeId = e3.Id,
            LeaveTypeId = lt.Id,
            StartDate = today,
            EndDate = today,
            TotalDays = 1,
            Status = LeaveStatus.APPROVED
        });

        // e4 has no record and no leave -> ABSENT
        await context.SaveChangesAsync();

        var service = new AttendanceService(context, _configuration, _logger);
        var adminContext = new CurrentUserContext { UserId = 999, Role = "ADMIN" };

        var summary = await service.GetTodaySummaryAsync(adminContext, today);

        Assert.Equal(4, summary.TotalEmployees);
        Assert.Equal(2, summary.PresentCount); // e1 and e2 present
        Assert.Equal(1, summary.LateCount);    // e2 late
        Assert.Equal(1, summary.OnLeaveCount); // e3 on leave
        Assert.Equal(1, summary.AbsentCount);  // e4 absent (BR-13)

        Assert.Single(summary.OnLeaveEmployees);
        Assert.Equal("EMP003", summary.OnLeaveEmployees[0].EmployeeCode);

        Assert.Single(summary.AbsentEmployees);
        Assert.Equal("EMP004", summary.AbsentEmployees[0].EmployeeCode);
    }
}
