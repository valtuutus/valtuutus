using FluentAssertions;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Lang.Tests;

public class LangSpecs
{
    [Fact]
    public void Full_Lang_v1_Specs_should_be_able_to_parse_entire_schema()
    {
        var parseResult = new SchemaReader().Parse(@"
entity user {}

entity organization {
    relation member @user;
    relation admin @user;
    attribute has_credit bool;
    permission view := has_credit and member;
}

entity repository {
    relation owner @organization#admin;
    relation organization @organization;
    attribute is_public bool;
    permission view := organization.admin or (is_public and organization.view);
    permission edit := organization.view;
}
");
        parseResult.IsT0.Should().BeTrue();
    }

    [Fact]
    public void Comma_is_required_after_entity_member_declaration()
    {
        var parseResult = new SchemaReader().Parse(@"entity user {
            relation test @group#member attribute nice bool
        }
");
        parseResult.AsT1.Should().NotBeEmpty();
        parseResult.AsT1.Should().BeEquivalentTo([
            new { Message = "src:ValtuutusParser - extraneous input 'attribute' expecting {'@', ';'}"},
            new { Message = "src:ValtuutusParser - missing ';' at '}'"}
        ]);
    }

    [Theory]
    [InlineData("entity user {}")]
    [InlineData("entity    user  {       }")]
    public void Empty_entity_different_whitespace_should_not_return_error(string schema)
    {
        // act
        var parseResult = new SchemaReader().Parse(
            schema);


        // assert
        var expected = new SchemaBuilder().WithEntity("user").SchemaBuilder.Build();
        parseResult.IsT0.Should().BeTrue();
        parseResult.AsT0.Should().BeEquivalentTo(expected
            );
    }

    [Fact]
    public void Should_parse_fn()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity account {

                relation owner @user;
                
                attribute balance int;

                permission withdraw := check_balance(context.amount, balance) and owner;
            }

            fn check_balance(amount int, balance int) =>
                (balance >= amount) and (amount <= 5000);
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_balance"].Execute(new Dictionary<string, object?>()
            {
                ["balance"] = 5000,
                ["amount"] = 5000
            }).Should()
            .BeTrue();
    }
    
    [Fact]
    public void Should_parse_fn2()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity account {

                relation owner @user;
                
                attribute balance int;

                permission withdraw := check_balance(context.amount, balance) and owner;
            }

            fn check_balance(amount int, balance int) =>
                (balance >= amount) and (amount <= 5000);
        ");

        schema.Should().NotBeNull();

        schema.IsT1.Should().BeFalse(string.Join(",", schema.AsT1.Select(x => x.ToString())));

        schema.AsT0.Functions["check_balance"].Execute(new Dictionary<string, object?>()
            {
                ["balance"] = null,
                ["amount"] = 500
            }).Should()
            .BeFalse();
    }

    [Fact]
    public void Should_parse_fn_with_int_comparison()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity account {

                relation owner @user;
                
                attribute balance int;

                permission withdraw := check_balance(context.amount, balance) and owner;
            }

            fn check_balance(amount int, balance int) =>
                (balance >= amount) and (amount <= 5000);
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_balance"].Execute(new Dictionary<string, object?>()
            {
                ["balance"] = 5000,
                ["amount"] = 5000
            }).Should()
            .BeTrue();
    }

    [Fact]
    public void Should_parse_fn_with_int_comparison_and_null_value()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity account {

                relation owner @user;
                
                attribute balance int;

                permission withdraw := check_balance(context.amount, balance) and owner;
            }

            fn check_balance(amount int, balance int) =>
                (balance >= amount) and (amount <= 5000);
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_balance"].Execute(new Dictionary<string, object?>()
            {
                ["balance"] = null,
                ["amount"] = 500
            }).Should()
            .BeFalse();
    }

    [Fact]
    public void Should_parse_fn_with_bool_comparison()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity account {

                relation owner @user;
                
                attribute is_active bool;

                permission access := check_active(is_active);
            }

            fn check_active(is_active bool) =>
                is_active == true;
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_active"].Execute(new Dictionary<string, object?>()
            {
                ["is_active"] = true
            }).Should()
            .BeTrue();
    }
    
    [Fact]
    public void Should_parse_fn_with_not_expression()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity account {

                relation owner @user;
                
                attribute is_active bool;

                permission access := not_check_active(is_active);
            }

            fn not_check_active(is_active bool) =>
                not(is_active == true);
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["not_check_active"].Execute(new Dictionary<string, object?>()
            {
                ["is_active"] = true
            }).Should()
            .BeFalse();
    }
    
    [Fact]
    public void Should_parse_fn_with_boolean_identifier_expression()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity account {

                relation owner @user;
                
                attribute is_active bool;

                permission access := check_active(is_active);
            }

            fn check_active(is_active bool) =>
                is_active;
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_active"].Execute(new Dictionary<string, object?>()
            {
                ["is_active"] = true
            }).Should()
            .BeTrue();
    }

    [Fact]
    public void Should_parse_fn_with_string_comparison()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity document {

                relation owner @user;
                
                attribute title string;

                permission view := check_title(title, ""Confidential"");
            }

            fn check_title(title string, requiredTitle string) =>
                title == requiredTitle;
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_title"].Execute(new Dictionary<string, object?>()
            {
                ["title"] = "Confidential",
                ["requiredTitle"] = "Confidential"
            }).Should()
            .BeTrue();
    }

    [Fact]
    public void Should_parse_fn_with_decimal_comparison()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity product {

                relation owner @user;
                
                attribute price decimal;

                permission purchase := check_price(price, 99.99);
            }

            fn check_price(price decimal, maxPrice decimal) =>
                price <= maxPrice;
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_price"].Execute(new Dictionary<string, object?>()
            {
                ["price"] = 99.99m,
                ["maxPrice"] = 99.99m
            }).Should()
            .BeTrue();
    }

    [Fact]
    public void Should_parse_fn_with_multiple_argument_types_and_conditions()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity transaction {

                relation owner @user;
                
                attribute amount int;
                attribute is_verified bool;
                attribute note string;
                attribute fee decimal;

                permission process := check_transaction(amount, is_verified, note, fee);
            }

            fn check_transaction(amount int, is_verified bool, note string, fee decimal) =>
                (amount > 100) and (is_verified == true) and (note == ""Valid"") and (fee <= 10.00);
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_transaction"].Execute(new Dictionary<string, object?>()
            {
                ["amount"] = 200,
                ["is_verified"] = true,
                ["note"] = "Valid",
                ["fee"] = 5.00m
            }).Should()
            .BeTrue();
    }

    [Fact]
    public void Should_parse_fn_with_multiple_argument_types_and_conditions_negative_case()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity transaction {

                relation owner @user;
                
                attribute amount int;
                attribute is_verified bool;
                attribute note string;
                attribute fee decimal;

                permission process := check_transaction(amount, is_verified, note, fee);
            }

            fn check_transaction(amount int, is_verified bool, note string, fee decimal) =>
                (amount > 100) and is_verified == true and (note == ""Valid"") and (fee <= 10.00);
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_transaction"].Execute(new Dictionary<string, object?>()
            {
                ["amount"] = 50,
                ["is_verified"] = false,
                ["note"] = "Invalid",
                ["fee"] = 15.00m
            }).Should()
            .BeFalse();
    }

    [Fact]
    public void Should_parse_fn_with_multiple_argument_types_and_partial_conditions()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity transaction {

                relation owner @user;
                
                attribute amount int;
                attribute is_verified bool;
                attribute note string;
                attribute fee decimal;

                permission process := check_transaction(amount, is_verified, note, fee);
            }

            fn check_transaction(amount int, is_verified bool, note string, fee decimal) =>
                (amount > 100) and is_verified == true and (note == ""Valid"") and (fee <= 10.00);
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_transaction"].Execute(new Dictionary<string, object?>()
            {
                ["amount"] = 200,
                ["is_verified"] = false,
                ["note"] = "Valid",
                ["fee"] = 5.00m
            }).Should()
            .BeFalse();
    }
    
    [Fact]
    public void Should_parse_fn_with_composable_logical_expressions()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity transaction {

                relation owner @user;

                attribute is_verified bool;
                attribute is_public bool;

                permission view := check_transaction(is_verified, is_public);
            }

            fn check_transaction(is_verified bool, is_public bool) =>
                is_verified == true or (is_public == true and not(is_verified));
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_transaction"].Execute(new Dictionary<string, object?>()
            {
                ["is_verified"] = false,
                ["is_public"] = true
            }).Should()
            .BeTrue();
    }
    
    [Fact]
    public void Should_parse_fn_with_composable_logical_expressions_with_identifier_boolean_expressions()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity transaction {

                relation owner @user;

                attribute is_verified bool;
                attribute is_public bool;

                permission view := check_transaction(is_verified, is_public);
            }

            fn check_transaction(is_verified bool, is_public bool) =>
                is_verified or (is_public and not(is_verified));
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_transaction"].Execute(new Dictionary<string, object?>()
            {
                ["is_verified"] = false,
                ["is_public"] = true
            }).Should()
            .BeTrue();
    }
    
    [Fact]
    public void Should_parse_fn_with_not_equal_expression()
    {
        var schema = new SchemaReader().Parse(@"
            fn not_deleted(status string) =>
                status != ""deleted"";
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["not_deleted"].Execute(new Dictionary<string, object?>()
            {
                ["status"] = "deleted",
            }).Should()
            .BeFalse();
    }
    
    [Fact]
    public void Should_parse_fn_with_less_than_expression()
    {
        var schema = new SchemaReader().Parse(@"
            fn within_threshold(value int, threshold int) =>
                value < threshold;
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["within_threshold"].Execute(new Dictionary<string, object?>()
            {
                ["value"] = 100,
                ["threshold"] = 1000,
            }).Should()
            .BeTrue();
    }
    
        
    [Fact]
    public void Should_parse_relation_with_multiple_referenced_entities_relations()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity organization {
                relation admin @user;
                relation member @user;
            }

            entity team {

                relation owner @user;
                relation member @user;
                relation org @organization;

                permission edit := org.admin or owner;
                permission delete := org.admin or owner;
                permission invite := org.admin and (owner or member);
                permission remove_user :=  owner;
            }

            entity project {

                relation members @team#member @team#owner @organization#member;
                relation team @team;
                relation org @organization;

                permission view := org.admin or team.member;
                permission edit := org.admin or team.member;
                permission delete := team.member;
            }
        ");

        schema.AsT0.Should().NotBeNull();

        schema.AsT0.Entities["project"].Relations["members"]
            .Entities
            .Should()
            .BeEquivalentTo([
                new RelationEntity() { Type = "team", Relation = "member", },
                new RelationEntity() { Type = "team", Relation = "owner", },
                new RelationEntity() { Type = "organization", Relation = "member", },
            ]);
    }
    
    [Fact]
    public void Schema_with_duplicate_entity_names_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}
            entity user {}
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Entity 'user' already declared in line 2:19.", }
        ]);
    }
    
    [Fact]
    public void Schema_with_relation_to_an_unknown_entity()
    {
        var schema = new SchemaReader().Parse(@"
            entity group {
                relation member @user;
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = $"Entity 'user' is not defined.", }
        ]);
    }
    
    [Fact]
    public void Schema_with_relation_to_an_unknown_entity_relation()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}
            entity group {
            }
            entity project {
                relation members @user @group#member;
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Entity 'group' 'member' relation is not defined.", }
        ]);
    }
    
    [Fact]
    public void Schema_with_relation_already_defined_in_entity()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}
            entity group {
                relation member @user;
            }
            entity project {
                relation member @user;
                relation member @group#member;
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Entity 'project' 'member' relation already been defined.", }
        ]);
    }
    
    [Fact]
    public void Schema_with_attribute_already_defined_in_entity()
    {
        var schema = new SchemaReader().Parse(@"
            entity project {
                attribute status string;
                attribute status int;
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Entity 'project' 'status' attribute already been defined.", }
        ]);
    }
}
