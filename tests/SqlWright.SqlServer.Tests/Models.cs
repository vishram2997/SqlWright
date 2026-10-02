using SqlWright.Mapping;

namespace SqlWright.SqlServer.Tests;

public enum UserStatus
{
    Pending = 0,
    Active = 1,
    Disabled = 2,
}

[Table("Users", Schema = "dbo")]
public class User
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string? Email { get; set; }
    public int? Age { get; set; }
    public UserStatus Status { get; set; }
    public decimal Balance { get; set; }
    [Computed] public DateTime CreatedAt { get; set; }
    [Computed] public byte[]? RowVer { get; set; }
}

[Table("Orders", Schema = "sales")]
public record Order(Guid Id, string CustomerName, decimal Total, DateTimeOffset PlacedAt);

[Table("order_lines")]
public class OrderLine
{
    [Key] public int OrderId { get; set; }
    [Key] public int LineNo { get; set; }
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
}

public class TypeSample
{
    public int Id { get; set; }
    public bool Flag { get; set; }
    public short? Small { get; set; }
    public long? Big { get; set; }
    public double? Ratio { get; set; }
    public decimal? Amount { get; set; }
    public DateOnly? Day { get; set; }
    public TimeOnly? Clock { get; set; }
    public DateTime? Stamp { get; set; }
    public byte[]? Payload { get; set; }
    public string? Code { get; set; }
    public string? Note { get; set; }
    public Guid? Ref { get; set; }
}
