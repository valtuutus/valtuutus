using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Schemas;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Data;
using Valtuutus.Core.Data;

namespace Valtuutus.Tests.Shared;

public abstract class BaseLookupEntityEngineSpecs : IAsyncLifetime
{
    protected BaseLookupEntityEngineSpecs(IDatabaseFixture fixture)
    {
        Fixture = fixture;
    }
    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);
    
    protected IDatabaseFixture Fixture { get; }
    
    private ServiceProvider CreateServiceProvider(Schema? schema = null)
    {
        var services = new ServiceCollection()
            .AddValtuutusCore(TestsConsts.Action);
        AddSpecificProvider(services)
            .AddConcurrentQueryLimit(5);
        
        if (schema != null)
        {
            var serviceDescriptor = services.First(descriptor => descriptor.ServiceType == typeof(Schema));
            services.Remove(serviceDescriptor);
            services.AddSingleton(schema);
        }

        return services.BuildServiceProvider();
    }

    private async ValueTask<ILookupEntityEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var lookupEntityEngine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();
        if(tuples.Length == 0 && attributes.Length == 0) return lookupEntityEngine;
        var provider = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await provider.Write(tuples, attributes, default);
        return lookupEntityEngine;
    }
    
    public async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, HashSet<string>>
        TopLevelChecks = LookupEntityEngineSpecList.TopLevelChecks;

    [Theory]
    [MemberData(nameof(TopLevelChecks))]
    public async Task TopLevelCheckShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        LookupEntityRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, HashSet<string>>
        IndirectRelationLookup = LookupEntityEngineSpecList.IndirectRelationLookup;

    [Theory]
    [MemberData(nameof(IndirectRelationLookup))]
    public async Task IndirectRelationLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, HashSet<string>>
        SimplePermissionLookup = LookupEntityEngineSpecList.SimplePermissionLookup;
    
    [Theory]
    [MemberData(nameof(SimplePermissionLookup))]
    public async Task SimplePermissionLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, HashSet<string>>
        IntersectWithRelationAndAttributePermissionLookup = LookupEntityEngineSpecList.IntersectWithRelationAndAttributePermissionLookup;
    
    [Theory]
    [MemberData(nameof(IntersectWithRelationAndAttributePermissionLookup))]
    public async Task IntersectWithRelationAndAttributeLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, HashSet<string>>
        IntersectAttributeExpressionWithOtherNodesLookup = LookupEntityEngineSpecList.IntersectAttributeExpressionWithOtherNodes;
    
    [Theory]
    [MemberData(nameof(IntersectAttributeExpressionWithOtherNodesLookup))]
    public async Task IntersectAttributeExpressionWithOtherNodesLookupShouldReturnExpectedEntities(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
    [Fact]
    public async Task TestStringBasedAttributeExpression()
    {
        // arrange
        var entity = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Workspaces.Identifier)
            .WithAttribute("status", typeof(string))
            .WithPermission("edit", PermissionNode.AttributeStringExpression("status", s => s == "active"));

        var schema = entity.SchemaBuilder.Build();

        // act
        var engine = await CreateEngine([], [
            new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "status",
                JsonValue.Create("active")!),
            new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "status",
                JsonValue.Create("active")!),
            new AttributeTuple(TestsConsts.Workspaces.Identifier, "1", "status",
                JsonValue.Create("archived")!),
        ], schema);

        // Act
        var result = await engine.LookupEntity(new LookupEntityRequest(TestsConsts.Workspaces.Identifier,
            "edit", "user", "1"), default);

        // assert
        result.Should().BeEquivalentTo(TestsConsts.Workspaces.PublicWorkspace, TestsConsts.Workspaces.PrivateWorkspace);
    }

    [Fact]
    public async Task TestDecimalBasedAttributeExpression()
    {
        // arrange
        var entity = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("account")
            .WithRelation("owner", c => c.WithEntityType(TestsConsts.Users.Identifier))
            .WithAttribute("balance", typeof(decimal))
            .WithPermission("withdraw", PermissionNode.Intersect(
                PermissionNode.Leaf("owner"),
                PermissionNode.AttributeDecimalExpression("balance", b => b >= 500m)
            ));

        var schema = entity.SchemaBuilder.Build();

        // act
        var engine = await CreateEngine(
            [
                new RelationTuple("account", "1", "owner", TestsConsts.Users.Identifier, "1"),
                new RelationTuple("account", "2", "owner", TestsConsts.Users.Identifier, "1")
            ],
            [
                new AttributeTuple("account", "1", "balance",
                    JsonValue.Create(872.54m)),
                new AttributeTuple("account", "2", "balance",
                    JsonValue.Create(12.11m)),
            ], schema);

        // Act
        var result = await engine.LookupEntity(new LookupEntityRequest("account",
            "withdraw", "user", "1"), default);

        // assert
        result.Should().BeEquivalentTo("1");
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, HashSet<string>>
        UnionRelationDepthLimit = LookupEntityEngineSpecList.UnionRelationDepthLimit;

    [Theory]
    [MemberData(nameof(UnionRelationDepthLimit))]
    public async Task LookupEntityWithDepthLimit(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, HashSet<string> expected)
    {
        // Arrange
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Groups.Identifier)
                .WithRelation("member", rc =>
                    rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithEntity(TestsConsts.Workspaces.Identifier)
                .WithRelation("group_members", rc =>
                    rc.WithEntityType(TestsConsts.Groups.Identifier))
                .WithPermission("view", PermissionNode.Leaf("group_members.member"))
            .SchemaBuilder.Build();
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.LookupEntity(request, default);

        // Assert
        result.Should().BeEquivalentTo(expected);
    }
}