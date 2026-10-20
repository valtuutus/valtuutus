# Valtuutus and caching

Valtuutus utilizes the amazing [FusionCache library](https://github.com/ZiggyCreatures/FusionCache) to power its caching capabilities.
You can set it the way you want, inmemory, inmemory+distributed or just distributed. 
Here we are going to show a basic way to use Fusion Cache with redis integrated with Valtuutus.
```csharp
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")!;

var muxxer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
builder.Services.AddFusionCache()
    .WithSerializer(
        new FusionCacheSystemTextJsonSerializer()
        )
    .WithDistributedCache( // <--- To use redis as a distributed cache provider
        new RedisCache(new RedisCacheOptions()
        {
            ConnectionMultiplexerFactory = () => Task.FromResult((IConnectionMultiplexer)muxxer)
        }))
    .WithBackplane( // <--- To automatically update other nodes
        new RedisBackplane(new RedisBackplaneOptions()
        {
            ConnectionMultiplexerFactory = () => Task.FromResult((IConnectionMultiplexer)muxxer)
        }));
```
After configuring Fusion Cache, you just need to call .AddCaching in the Valtuutus setup:
```csharp
builder.Services.AddValtuutusCore("""
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
                permission remove_user := owner;
            }
            entity project {
                relation org @organization;
                relation team @team;
                relation member @team#member @user;
                attribute public bool;
                attribute status int;
                permission view := org.admin or member or (public and org.member);
                permission edit := (org.admin or team.member) and isActiveStatus(status);
                permission delete := team.member;
            }
            
            fn isActiveStatus(status int) => status == 1;
""")
    .AddPostgres(_ => () => new NpgsqlConnection(builder.Configuration.GetConnectionString("PostgresDb")!))
    .AddConcurrentQueryLimit(3)
    .AddCaching() // <--- Here
;
```
That's it. Now all your queries to the engines will be cached to reduce the load in your database. (It is not required to use a database provider to use caching)

## How cached requests pick a snap token

A request with a `SnapToken` is cached under that token. A request without one reads at the latest token the cache knows, so a cached answer needs no database round trip. When the cache knows no latest token (on first use, or after it expires), the engine resolves it in its first round trip, and the result is cached under the token the engine reports. Writes and deletes made through Valtuutus update the cache's latest token once they have committed; one made in your own transaction (an `IDbDataWriterProvider` overload that takes a transaction) does so when you call `NotifyCommitted` after committing it. The latest token only moves forward: a write that finishes after a newer one doesn't replace it.

`AddCaching` updates the latest token through `ValtuutusDataOptions.OnDataWritten`. A hook you set before calling `AddCaching` keeps running alongside it; one you set afterwards replaces it, so set your own hook first.

## Registration order

`AddCaching` decorates the engines registered at the point it runs, so call it after
`AddValtuutusCore` — the end of the registration chain, as in the example above.

## Multi node scenario
⚠️ If you are writing/deleting data from a multi node scenario, it is highly recommended to use a backplane for Fusion Cache. 
That way, any writes/deletes in the data will automatically invalidate the cache in all instances. A write or delete returns once the backplane notification for the new latest token has been sent, so a request that lands on another instance right after it already reads at the new token.
[Click here for more information.](https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/Backplane.md)
