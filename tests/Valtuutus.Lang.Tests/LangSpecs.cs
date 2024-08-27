using FluentAssertions;

namespace Valtuutus.Lang.Tests;

public class LangSpecs
{
    [Fact(Skip = "Functions are still not implemented")]
    public void Full_Lang_Specs_should_be_able_to_parse_entire_schema()
    {
        var schema = SchemaReader.Parse(@"
entity user {}

entity organization {
    relation member @user
    relation admin @user
    attribute credit int
    permission view := check_credit(credit) and member
}

entity repository {
    relation owner @organization#admin
    relation organization @organization
    attribute is_public bool
    permission view := organization.admin or (is_public and organization.view)
    permission edit := organization.view
    permission delete := is_weekday(request.day_of_week)
}
fn check_credit(credit int) {
    credit > 5000
}
fn is_weekday(day_of_week string) {
    day_of_week != 'saturday' && day_of_week != 'sunday'
}
");

        schema.Should().NotBeNull();
    }

    [Fact]
    public void Invalid_schema_Should_Throw_Exception()
    {
        Action act = () => SchemaReader.Parse(@"entity user {
            relation
");
        act.Should().Throw<SchemaParseException>();
    }
}