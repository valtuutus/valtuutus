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

## Multi node scenario
⚠️ If you are writing/deleting data from a multi node scenario, it is highly recommended to use a backplane for Fusion Cache. 
That way, any writes/deletes in the data will automatically invalidate the cache in all instances.
[Click here for more information.](https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/Backplane.md)

