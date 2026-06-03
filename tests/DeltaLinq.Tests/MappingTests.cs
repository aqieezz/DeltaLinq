using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

public class MappingTests
{
    [Fact]
    public void Column_attribute_renames_column()
    {
        var model = EntityModel.For(typeof(User));
        Assert.Contains(model.Columns, c => c.Property.Name == "SignupDate" && c.ColumnName == "signup_date");
    }

    [Fact]
    public void NotMapped_and_readonly_properties_are_excluded()
    {
        var model = EntityModel.For(typeof(User));
        Assert.DoesNotContain(model.Columns, c => c.Property.Name == "DisplayName");
    }

    [Fact]
    public void Default_column_name_is_property_name()
    {
        var model = EntityModel.For(typeof(User));
        Assert.Contains(model.Columns, c => c.ColumnName == "Name");
        Assert.Contains(model.Columns, c => c.ColumnName == "Price");
    }
}
