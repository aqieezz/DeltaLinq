using System.ComponentModel.DataAnnotations.Schema;

namespace DeltaLinq.Sample.Api;

/// <summary>Entity mapped to the demo Delta table. [Column] renames SignupDate → signup_date.</summary>
public sealed class User
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    [Column("signup_date")] public DateTime SignupDate { get; set; }
    public double Price { get; set; }
    public bool IsActive { get; set; }
}
