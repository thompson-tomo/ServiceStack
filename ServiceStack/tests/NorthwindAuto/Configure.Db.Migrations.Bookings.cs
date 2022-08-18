using ServiceStack;
using ServiceStack.DataAnnotations;
using ServiceStack.OrmLite;
using ServiceStack.OrmLite.Tests.Migrations;

namespace MyApp;

// Current App Version
public class Booking : AuditBase
{
    [AutoIncrement]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public RoomType RoomType { get; set; }
    public int RoomNumber { get; set; }
    public DateTime BookingStartDate { get; set; }
    public DateTime? BookingEndDate { get; set; }
    public decimal Cost { get; set; }
    public string? Notes { get; set; }
    public bool? Cancelled { get; set; }
}
public enum RoomType
{
    Single,
    Double,
    Queen,
    Twin,
    Suite,
}

public class Migration1000 : MigrationBase
{
    // Initial Version
    public class Booking : AuditBase
    {
        [AutoIncrement]
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public RoomType RoomType { get; set; }
        public int RoomNumber { get; set; }
        public DateTime BookingStartDate { get; set; }
        public DateTime? BookingEndDate { get; set; }
        public decimal Cost { get; set; }
        public string? Notes { get; set; }
        public bool? Cancelled { get; set; }
    }
    public enum RoomType
    {
        Single,
        Double,
        Queen,
        Twin,
        Suite,
    }

    public override void Up()
    {
        Db.CreateTable<Booking>();
        CreateBooking("First Booking!", RoomType.Queen, 10, 100, "employee@email.com");
        CreateBooking("Booking 2", RoomType.Double, 12, 120, "manager@email.com");
        CreateBooking("Booking the 3rd", RoomType.Suite, 13, 130, "employee@email.com");
    }

    public override void Down()
    {
        Db.DropTable<Booking>();
    }

    static int bookingId = 0;
    public void CreateBooking(string name, RoomType type, int roomNo, decimal cost, string by) =>
        Db.Insert(new Booking {
            Id = ++bookingId,
            Name = name,
            RoomType = type,
            RoomNumber = roomNo,
            Cost = cost,
            BookingStartDate = DateTime.UtcNow.AddDays(bookingId),
            BookingEndDate = DateTime.UtcNow.AddDays(bookingId + 7),
        }.WithAudit(by));
}