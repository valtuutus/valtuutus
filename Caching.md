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
builder.Services.AddValtuutusCore(c =>
    {
        c
            .WithEntity("user")
            .WithEntity("organization")
                .WithRelation("admin", rc => rc.WithEntityType("user"))
                .WithRelation("member", rc => rc.WithEntityType("user"))
            .WithEntity("team")
                .WithRelation("owner", rc => rc.WithEntityType("user"))
                .WithRelation("member", rc => rc.WithEntityType("user"))
                .WithRelation("org", rc => rc.WithEntityType("organization"))
                .WithPermission("edit", PermissionNode.Union("org.admin", "owner"))
                .WithPermission("delete", PermissionNode.Union("org.admin", "owner"))
                .WithPermission("invite", PermissionNode.Intersect("org.admin", PermissionNode.Union("owner", "member")))
                .WithPermission("remove_user", PermissionNode.Leaf("owner"))
            .WithEntity("project")
                .WithRelation("org", rc => rc.WithEntityType("organization"))
                .WithRelation("team", rc => rc.WithEntityType("team"))
                .WithRelation("member", rc => rc.WithEntityType("team", "member").WithEntityType("user"))
                .WithAttribute("public", typeof(bool))
                .WithAttribute("status", typeof(string))
                .WithPermission("view",
                    PermissionNode.Union(
                        PermissionNode.Leaf("org.admin"),
                        PermissionNode.Leaf("member"),
                        PermissionNode.Intersect("public", "org.member"))
                )
                .WithPermission("edit", PermissionNode.Intersect(
                    PermissionNode.Union("org.admin", "team.member"),
                    PermissionNode.AttributeStringExpression("status", status => status == "ativo"))
                )
                .WithPermission("delete", PermissionNode.Leaf("team.member"));
    })
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

