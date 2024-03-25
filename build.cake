var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////


Task("Restore")
    .Description("Restoring the solution dependencies")
    .Does(() => {
           var projects = GetFiles("./src/**/*.csproj");

              foreach(var project in projects )
              {
                  Information($"Building { project.ToString()}");
                  DotNetRestore(project.ToString());
              } 
});


Task("Build")
    .IsDependentOn("Restore")
    .Does(() => {
     var buildSettings = new DotNetBuildSettings {
                        Configuration = configuration,
                        MSBuildSettings = new DotNetMSBuildSettings()
                       };
     var projects = GetFiles("./src/**/*.csproj");
     foreach(var project in projects )
     {
         Information($"Building {project.ToString()}");
         DotNetBuild(project.ToString(),buildSettings);
     }
});


Task("Pack")
 .IsDependentOn("Build")
 .Does(() => {
 
   var settings = new DotNetPackSettings
    {
        Configuration = configuration,
        OutputDirectory = "./.artifacts",
        NoBuild = true,
        NoRestore = true,
        MSBuildSettings = new DotNetMSBuildSettings()
                        .WithProperty("Copyright", $"© Copyright Valtuutus {DateTime.Now.Year}")
    };
    
    DotNetPack("./Valtuutus.sln", settings);
 });


Task("PublishNuget")
 .IsDependentOn("Pack")
 .Does(context => {
   if (BuildSystem.GitHubActions.IsRunningOnGitHubActions)
   {
     foreach(var file in GetFiles("./.artifacts/*.nupkg"))
     {
       Information("Publishing {0}...", file.GetFilename().FullPath);
       DotNetNuGetPush(file, new DotNetNuGetPushSettings {
          ApiKey = context.EnvironmentVariable("NUGET_API_KEY"),
          Source = "https://api.nuget.org/v3/index.json"
       });
     }
   }
 }); 



//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

Task("Default")
       .IsDependentOn("Restore")
       .IsDependentOn("Build")
       .IsDependentOn("Pack")
       .IsDependentOn("PublishNuget");

RunTarget(target);