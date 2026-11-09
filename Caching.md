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

A request without a `SnapToken` reads at the latest token the cache knows, so a cached answer needs no database round trip. That token is always a **settled** one: on Postgres, SQL Server, MySQL, MariaDB and InMemory no write can still commit at or under it, so the answer at it never changes. A custom `IDataReaderProvider` must keep the same promise: `GetLatestSnapToken` returns a token no write can still commit at or under. When the cache knows no latest token (on first use, or after it expires), the engine resolves the latest settled token in its first round trip, and the result is cached under it. Writes and deletes made through Valtuutus move the cache's latest token forward once they have committed, to the latest settled token read right after the commit; one made in your own transaction (an `IDbDataWriterProvider` overload that takes a transaction) does so when you call `NotifyCommitted` after committing it. The latest token only moves forward.

A request with a `SnapToken` is cached under that token when the token is at or below the latest one the cache knows. A newer token (typically the one a write just returned, while a write that took its id earlier is still open) is answered without the cache: rows can still land under it.

`AddCaching` updates the latest token by adding a handler to `ValtuutusDataOptions.OnDataWritten`. Handlers you add (`options.OnDataWritten.Add(...)`) run alongside it, in the order added, whether you add them before or after calling `AddCaching`.

## Cache durations

```csharp
.AddCaching(options =>
{
    options.Duration = TimeSpan.FromMinutes(10);               // default: 5 minutes
    options.LatestSnapTokenDuration = TimeSpan.FromSeconds(30); // default: 5 minutes
})
```

- `Duration` is how long a result is cached. Results are cached under the snap token they were read at, and the data at a token doesn't change, so a longer duration keeps more entries and reads the database less often without serving older answers.
- `LatestSnapTokenDuration` is how long the latest token is kept. Requests without a token read at it, and only writes made through Valtuutus move it forward. A write made to the tables some other way (another application, a script, a migration) is missed by those requests until the latest token expires, so this duration bounds that delay.

These durations set the expiry of Valtuutus's entries; the rest of your FusionCache entry options (fail-safe, distributed cache, backplane) still apply.

## Reading your own writes

A request without a token reads at the latest token the cache knows, which can be slightly behind a write made on another node. To read your own write, pass the `SnapToken` that `Write`/`Delete` returns.

## Registration order

`AddCaching` decorates the engines registered at the point it runs, so call it after
`AddValtuutusCore` — the end of the registration chain, as in the example above.

## Multi node scenario
⚠️ If you are writing/deleting data from a multi node scenario, it is highly recommended to use a backplane for Fusion Cache. 
That way, any writes/deletes in the data will automatically invalidate the cache in all instances. A write or delete returns once the backplane notification for the new latest token has been sent, so a request that lands on another instance right after it already reads at the new token.
[Click here for more information.](https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/Backplane.md)
