var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var packageVersion = Argument("packageVersion", "");
var releaseNotesPropsFile = Argument("releaseNotesPropsFile", "");

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

   var msbuild = new DotNetMSBuildSettings()
       .WithProperty("Copyright", $"© Copyright Valtuutus {DateTime.Now.Year}");

   if (!string.IsNullOrEmpty(packageVersion))
       msbuild = msbuild.WithProperty("PackageVersion", packageVersion);

   if (!string.IsNullOrEmpty(releaseNotesPropsFile))
       msbuild = msbuild.WithProperty("CustomAfterMicrosoftCommonProps", releaseNotesPropsFile);

   var settings = new DotNetPackSettings
    {
        Configuration = configuration,
        OutputDirectory = "./.artifacts",
        NoBuild = true,
        NoRestore = true,
        MSBuildSettings = msbuild
    };

    DotNetPack("./Valtuutus.sln", settings);
 });


Task("PublishNuget")
 .IsDependentOn("Pack")
 .Does(context => {
   if (BuildSystem.GitHubActions.IsRunningOnGitHubActions)
   {
       DotNetNuGetPush("./.artifacts/*.nupkg", new DotNetNuGetPushSettings {
          ApiKey = context.EnvironmentVariable("NUGET_API_KEY"),
          Source = "https://api.nuget.org/v3/index.json",
          SkipDuplicate = true
       });
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
