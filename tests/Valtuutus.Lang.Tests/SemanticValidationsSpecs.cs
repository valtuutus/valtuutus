using FluentAssertions;
using Valtuutus.Core.Lang.SchemaReaders;

namespace Valtuutus.Lang.Tests;

public class LangSemanticValidationsSpecs
{
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


    [Fact]
    public void Schema_with_functions_with_duplicate_names_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value int) =>
            value == 2;

        fn check_value(amount int) =>
            amount == 10;
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Function with name 'check_value' already defined.", }
        ]);
    }

    [Fact]
    public void Schema_with_function_returning_non_boolean_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        entity product {
            attribute price decimal;
        }

        fn get_price(price decimal) => price;
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected boolean parameter, got decimal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_returning_non_boolean_literal_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        entity product {
            attribute price decimal;
        }

        fn get_price() => ""legal"";
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected boolean literal, got \"legal\"", }
        ]);
    }

    [Fact]
    public void Schema_with_function_comparing_incompatible_types_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn get_price(name string, value int ) => name >= value;
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Incompatible types, comparing string and int", }
        ]);
    }

    [Fact]
    public void Schema_with_function_using_unknown_parameter()
    {
        var schema = new SchemaReader().Parse(@"
        fn get_price(name string) => teste;
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "teste is not defined in the function context.", }
        ]);
    }

    [Fact]
    public void Schema_with_permission_using_undefined_relation()
    {
        var schema = new SchemaReader().Parse(@"
            entity company {
                attribute public bool;
            }

            entity product {
                
                permission view := company.public;
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Undefined relation with name: company.", }
        ]);
    }

    [Fact]
    public void Schema_with_permission_using_undefined_deep_relation()
    {
        var schema = new SchemaReader().Parse(@"
            entity organization {
            }
            
            entity company {
                relation org @organization;
            }

            entity product {
                relation org @company#org;
                permission view := org.member;
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Undefined relation, attribute or permission with name: member for entity organization", }
        ]);
    }

    [Fact]
    public void Schema_with_permission_using_undefined_own_relation()
    {
        var schema = new SchemaReader().Parse(@"

            entity product {
                permission view := available;
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Undefined relation, attribute or permission with name: available", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_with_insufficient_arguments_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
            fn check_value(value int) =>
                value == 2;

            entity test {
                permission view := check_value();
            }
        ");

        schema.IsT0.Should().BeFalse();
        

        schema.AsT1.Should().BeEquivalentTo([
            new
            {
                Message =
                    "check_value invoked with wrong number of arguments, expected 1 got 0",
            }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_with_excess_arguments_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
            fn check_value(value int) =>
                value == 2;

            entity test {
                permission view := check_value(10, 20);
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new
            {
                Message =
                    "check_value invoked with wrong number of arguments, expected 1 got 2",
            }
        ]);
    }


    [Fact]
    public void Schema_with_function_call_using_undefined_attribute_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        entity product {
            attribute price decimal;
            permission view := check_price(non_existent_attribute);
        }

        fn check_price(price decimal) => price > 10.0;
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Undefined attribute non_existent_attribute", }
        ]);
    }


    [Fact]
    public void Schema_with_function_call_using_attribute_with_wrong_type_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        entity product {
            attribute price string;
            permission view := check_price(price);
        }

        fn check_price(price decimal) => price > 10.0;
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type decimal, but got a string attribute", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_with_multiple_arguments_and_one_wrong_type_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        entity product {
            attribute price decimal;
            attribute description int;
            permission view := check_product(price, description);
        }

        fn check_product(price decimal, description string) => price > 10.0 and description == ""valid"";
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type string, but got a int attribute", }
        ]);
    }


    [Fact]
    public void Schema_with_function_call_passing_string_literal_to_int_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value int) => value > 0;

        entity test {
            permission view := check_value(""string_literal"");
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type int, but got a string literal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_passing_int_literal_to_string_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value string) => value == ""valid"";

        entity test {
            permission view := check_value(123);
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type string, but got a int literal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_passing_decimal_literal_to_string_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value string) => value == ""valid"";

        entity test {
            permission view := check_value(123.45);
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type string, but got a decimal literal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_passing_bool_literal_to_string_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value string) => value == ""valid"";

        entity test {
            permission view := check_value(true);
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type string, but got a bool literal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_passing_string_literal_to_decimal_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value decimal) => value > 0.0;

        entity test {
            permission view := check_value(""string_literal"");
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type decimal, but got a string literal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_passing_int_literal_to_decimal_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value decimal) => value > 0.0;

        entity test {
            permission view := check_value(123);
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type decimal, but got a int literal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_passing_bool_literal_to_decimal_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value decimal) => value > 0.0;

        entity test {
            permission view := check_value(true);
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type decimal, but got a bool literal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_passing_string_literal_to_bool_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value bool) => value;

        entity test {
            permission view := check_value(""string_literal"");
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type bool, but got a string literal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_passing_int_literal_to_bool_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value bool) => value;

        entity test {
            permission view := check_value(123);
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type bool, but got a int literal", }
        ]);
    }

    [Fact]
    public void Schema_with_function_call_passing_decimal_literal_to_bool_param_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        fn check_value(value bool) => value;

        entity test {
            permission view := check_value(123.45);
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Expected type bool, but got a decimal literal", }
        ]);
    }

    [Fact]
    public void Schema_with_relation_with_different_entities_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        entity user {}
        entity group {
            relation member @user;
        }
        entity project {
            relation member @user @group;
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Entity 'project' 'member' has inconsistent final entity references.", }
        ]);
    }

    [Fact]
    public void Schema_with_relation_with_different_entities_deep_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
        entity user {}
        entity group {
            relation member @user;
        }
        entity workspace {
            relation member @group;
        }
        entity team {
            relation member @user;
        }
        entity project {
            relation member @team#member @workspace#member;
        }
    ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Entity 'project' 'member' has inconsistent final entity references.", }
        ]);
    }

    [Fact]
    public void Schema_with_call_to_non_existent_function_in_permission_should_return_error()
    {
        var schema = new SchemaReader().Parse(@"
            entity product {
                attribute price decimal;
                permission view := non_existent_function();
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "non_existent_function is not a defined function", }
        ]);
    }

    [Fact]
    public void Schema_with_permission_using_relation_with_undefined_subrelation()
    {
        var schema = new SchemaReader().Parse(@"
            entity organization {

            }
            entity project {
                relation org @organization;
                permission view := org.member;
            }
        ");

        schema.IsT0.Should().BeFalse();

        schema.AsT1.Should().BeEquivalentTo([
            new { Message = "Undefined relation, attribute or permission with name: member for entity organization", }
        ]);
    }
}