namespace SqlWright.Tests;

public class DiagnosticsTests : Database
{
    public DiagnosticsTests() => SeedUsers();

    public class Person
    {
        public long Id { get; set; }
        public string FirstName { get; set; } = "";
        public int Age { get; set; }
    }

    public record PersonRecord(long Id, string FirstName);

    [Fact]
    public void Conversion_failure_names_the_column_member_and_value()
    {
        var ex = Assert.Throws<MappingException>(() =>
            Connection.Query<Person>("SELECT Id, 'thirty' AS Age FROM Users").ToList());

        Assert.Equal("Age", ex.ColumnName);
        Assert.Equal(1, ex.ColumnOrdinal);
        Assert.Equal("Person.Age", ex.MemberName);
        Assert.Contains("'thirty'", ex.Message);
        Assert.Contains("Int32", ex.Message);
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Scalar_conversion_failure_names_the_column()
    {
        var ex = Assert.Throws<MappingException>(() => Connection.QueryFirst<int>("SELECT first_name FROM Users"));
        Assert.Equal("first_name", ex.ColumnName);
        Assert.Contains("'Ada'", ex.Message);
    }

    [Fact]
    public void No_matching_columns_is_an_error_with_a_suggestion()
    {
        var ex = Assert.Throws<MappingException>(() => Connection.Query<Person>("SELECT first_name FROM Users").ToList());

        Assert.Contains("None of the columns ('first_name')", ex.Message);
        Assert.Contains("Did you mean 'FirstName'?", ex.Message);
        Assert.Contains("MatchNamesWithUnderscores", ex.Message);
    }

    [Fact]
    public void Strict_mapping_reports_unmapped_columns_with_typo_suggestions()
    {
        Assert.Single(Connection.Query<Person>("SELECT Id, first_name AS FirstNmae FROM Users WHERE Id = 1")); // lenient by default

        SqlWrightSettings.StrictMapping = true;
        var ex = Assert.Throws<MappingException>(() =>
            Connection.Query<Person>("SELECT Id, first_name AS FirstNmae FROM Users WHERE Id = 1").ToList());

        Assert.Equal("FirstNmae", ex.ColumnName);
        Assert.Contains("Did you mean 'FirstName'?", ex.Message);
    }

    [Fact]
    public void Constructor_mismatch_lists_what_is_missing()
    {
        var ex = Assert.Throws<MappingException>(() =>
            Connection.Query<PersonRecord>("SELECT Id, first_name AS FirstNam FROM Users").ToList());

        Assert.Contains("(Id, FirstName): no column for 'FirstName' (closest column: 'FirstNam')", ex.Message);
    }

    [Fact]
    public void Row_count_errors_say_what_was_expected()
    {
        var ex = Assert.Throws<SqlWrightException>(() => Connection.QuerySingle<Person>("SELECT * FROM Users"));
        Assert.Contains("Single<Person> expected exactly one row, but the query returned more than one", ex.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(ex); // still catchable the Dapper way
    }
}
