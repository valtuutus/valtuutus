using FluentAssertions;
using NSubstitute;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory.Tests;


public sealed class DataEngineSpecs
{
    private static DataEngine CreateEngine()
    {
        return new DataEngine(Substitute.For<IDataWriterProvider>());
    }
    
    [Fact]
    public async Task Writing_empty_data_should_throw()
    {
        // arrange
        var dataEngine = CreateEngine();

        // act
        Func<Task> act = async () => await dataEngine.Write(Array.Empty<RelationTuple>(), Array.Empty<AttributeTuple>(), default);

        // assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
    
    [Fact]
    public async Task Deleting_empty_data_should_throw()
    {
        // arrange
        var dataEngine = CreateEngine();

        // act
        Func<Task> act = async () => await dataEngine.Delete(new DeleteFilter(), default);

        // assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}