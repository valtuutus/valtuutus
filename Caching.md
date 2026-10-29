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

`AddCaching` updates the latest token by adding a handler to `ValtuutusDataOptions.OnDataWritten`. Handlers you add (`options.OnDataWritten.Add(...)`) run alongside it, in the order added, whether you add them before or after calling `AddCaching`.

## Cache durations

```csharp
.AddCaching(options =>
{
    options.Duration = TimeSpan.FromMinutes(10);               // default: 5 minutes
    options.LatestSnapTokenDuration = TimeSpan.FromSeconds(30); // default: 5 minutes
})
```

- `Duration` is how long a result is cached. Results are cached under the snap token they were read at, and the data at a token doesn't change, so a longer duration keeps more entries and reads the database less often without serving older answers (see the settle window below for the one exception it bounds).
- `LatestSnapTokenDuration` is how long the latest token is kept. Requests without a token read at it, and only writes made through Valtuutus move it forward. A write made to the tables some other way (another application, a script, a migration) is missed by those requests until the latest token expires, so this duration bounds that delay.

These durations set the expiry of Valtuutus's entries; the rest of your FusionCache entry options (fail-safe, distributed cache, backplane) still apply.

## Settle window

A writer takes its transaction id (the snap token its rows are written at) before it commits. When two writes run concurrently, the one that took the older id can commit after the newer one, so its rows land at a token older than one requests have already read at. A result cached at that newer token would miss those rows until it expires.

To bound this, a result read at a token younger than the settle window (measured from the timestamp in the token's ULID) is cached for one second instead of the full `Duration`. A write that commits late does so within the window of taking its id, so any answer read before it was read at a young token and expires within a second. Results at older tokens are cached for the full duration, as before. A token that is not a ULID counts as older than the window.

```csharp
.AddCaching(options =>
{
    options.SettleWindow = TimeSpan.FromSeconds(10); // default: 5 seconds
})
```

`TimeSpan.Zero` turns the settle window off and caches every result for the full duration.

- **Cache hits:** only in the window after a write, a repeated request at the newest token misses about once per second instead of hitting until the next write, so at most about `SettleWindow / 1s` extra engine runs per distinct hot request per write. When writes arrive more often than the window, results stay in one-second entries, but there they only lived until the next write anyway.
- **Clock:** a token's age is measured with `ValtuutusCachingOptions.TimeProvider`, which `AddCaching` sets to the `TimeProvider` registered in your service collection (`TimeProvider.System` otherwise). Ages compare the reading node's clock with the writing node's: when the reading node's clock is ahead, the window shrinks by that difference; when it is behind, tokens look younger and are cached for less.
- **Long transactions:** a write that commits more than the window after taking its id (a long transaction you own, passed to an `IDbDataWriterProvider` overload) can still be missed by a result cached at a newer token. Keep those transactions short, or raise the window.
- **Reading your own writes:** a request without a token reads at the latest token the cache knows, which can be slightly behind a write made on another node. To read your own write, pass the `SnapToken` that `Write`/`Delete` returns.

## Registration order

`AddCaching` decorates the engines registered at the point it runs, so call it after
`AddValtuutusCore` — the end of the registration chain, as in the example above.

## Multi node scenario
⚠️ If you are writing/deleting data from a multi node scenario, it is highly recommended to use a backplane for Fusion Cache. 
That way, any writes/deletes in the data will automatically invalidate the cache in all instances. A write or delete returns once the backplane notification for the new latest token has been sent, so a request that lands on another instance right after it already reads at the new token.
[Click here for more information.](https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/Backplane.md)
