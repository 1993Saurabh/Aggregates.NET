// Install addins.
#addin "nuget:?package=Cake.FileHelpers&version=2.0.0"
#addin "nuget:?package=Cake.Coveralls&version=0.7.0"
#addin "nuget:?package=Cake.Powershell&version=0.4.3"
#addin "nuget:?package=Cake.Incubator&version=1.6.0"
#addin "nuget:?package=Cake.Docker&version=0.8.2"
#addin "nuget:?package=Cake.Curl&version=2.0.0"

// Install tools.
#tool "nuget:?package=GitReleaseManager&version=0.6.0"
#tool "nuget:?package=GitVersion.CommandLine&version=3.6.5"
#tool "nuget:?package=coveralls.io&version=1.3.4"
#tool "nuget:?package=OpenCover&version=4.6.519"
#tool "nuget:?package=ReportGenerator&version=3.0.2"
#tool "nuget:?package=gitlink&version=3.1.0"
#tool "nuget:?package=NUnit.ConsoleRunner&version=3.7.0"

// Load other scripts.
#load "./build/parameters.cake"

///////////////////////////////////////////////////////////////////////////////
// USINGS
///////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

//////////////////////////////////////////////////////////////////////
// PARAMETERS
//////////////////////////////////////////////////////////////////////

BuildParameters parameters = BuildParameters.GetParameters(Context);

string GetDotNetCoreArgsVersions(BuildVersion version)
{

    return string.Format(
        @"/p:Version={1} /p:AssemblyVersion={0} /p:FileVersion={0} /p:ProductVersion={0}",
        version.SemVersion, version.NuGet);
}

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(context =>
{
    parameters.Initialize(context);

    Information("==============================================");
    Information("==============================================");

    Information("Solution: " + parameters.Solution);
    Information("Target: " + parameters.Target);
    Information("Configuration: " + parameters.Configuration);
    Information("IsLocalBuild: " + parameters.IsLocalBuild);
    Information("IsRunningOnUnix: " + parameters.IsRunningOnUnix);
    Information("IsRunningOnWindows: " + parameters.IsRunningOnWindows);
    Information("IsRunningOnVSTS: " + parameters.IsRunningOnVSTS);
    Information("IsReleaseBuild: " + parameters.IsReleaseBuild);
    Information("IsMaster: " + parameters.IsMaster);
    Information("IsPullRequest: " + parameters.IsPullRequest);
    Information("BuildNumber: " + parameters.BuildNumber);

    // Increase verbosity?
    if(parameters.IsReleaseBuild && (context.Log.Verbosity != Verbosity.Diagnostic)) {
        Information("Increasing verbosity to diagnostic.");
        context.Log.Verbosity = Verbosity.Diagnostic;
    }

    Information("Building version {0} {5} of {4} ({1}, {2}) using version {3} of Cake",
        parameters.Version.SemVersion,
        parameters.Configuration,
        parameters.Target,
        parameters.Version.CakeVersion,
        parameters.Solution,
        parameters.Version.Sha.Substring(0,8));

});


///////////////////////////////////////////////////////////////////////////////
// TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Teardown(context =>
{
    Information("Finished running tasks.");

    if(!context.Successful)
    {
        Error(string.Format("Exception: {0} Message: {1}\nStack: {2}", context.ThrownException.GetType(), context.ThrownException.Message, context.ThrownException.StackTrace));
    }
});


//////////////////////////////////////////////////////////////////////
// DEFINITIONS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    CleanDirectories(parameters.Paths.Directories.ToClean);
});

Task("Restore-NuGet-Packages")
    .IsDependentOn("Clean")
    .Does(() =>
{
    DotNetCoreRestore(parameters.Solution.FullPath, new DotNetCoreRestoreSettings()
                {
                    ConfigFile = new FilePath("./build/nuget.config"),
                    ArgumentCustomization = aggs => aggs.Append(GetDotNetCoreArgsVersions(parameters.Version))
                });


});

Task("Update-NuGet-Packages")
    .IsDependentOn("Restore-NuGet-Packages")
    .Does(() =>
{


    // Update all our packages to latest build version
    NuGetUpdate(parameters.Solution.FullPath, new NuGetUpdateSettings {
        Safe = true,
        ArgumentCustomization = args => args.Append("-FileConflictAction Overwrite")
    });
});

Task("Build")
    .IsDependentOn("Restore-NuGet-Packages")
    .Does(() =>
{   

    DotNetCoreBuild(parameters.Solution.FullPath,
    new DotNetCoreBuildSettings {
        Configuration = parameters.Configuration,
        ArgumentCustomization = aggs => aggs
                .Append(GetDotNetCoreArgsVersions(parameters.Version))
                .Append("/p:ci=true")
                .Append("/p:SourceLinkEnabled=true")
    });


});

Task("Run-Unit-Tests")
    .IsDependentOn("Build")
    .Does(() =>
{
    EnsureDirectoryExists(parameters.Paths.Directories.TestResultsDir);
    NUnit3("./src/**/bin/" + parameters.Configuration + "/**/*Tests.dll",
                new NUnit3Settings
                {
                    Timeout = 600000,
                    ShadowCopy = false,
                    NoHeader = true,
                    NoColor = true,
                    DisposeRunners = true,
                    OutputFile = parameters.Paths.Directories.TestResultsDir.CombineWithFilePath("./TestOutput.txt"),
                    NoResults = true
                });
    
//      Action<ICakeContext> testAction = tool => {

//        tool.NUnit3("./src/**/bin/" + parameters.Configuration + "/**/*Tests.dll",
//                new NUnit3Settings
//                {
//                    Timeout = 600000,
//                    ShadowCopy = false,
//                    NoHeader = true,
//                    NoColor = true,
//                    DisposeRunners = true,
//                    OutputFile = parameters.Paths.Directories.TestResultsDir.CombineWithFilePath("./TestOutput.txt"),
//                    NoResults = true
//                });
//    };

//    OpenCover(testAction,
//        parameters.Paths.Directories.TestResultsDir.CombineWithFilePath("./OpenCover.xml"),
//        new OpenCoverSettings {
//            ReturnTargetCodeOffset = 0,
//            ArgumentCustomization = aggs => aggs.Append("-register")
//        }
//        .WithFilter("+[Aggregates.NET*]*")
//        .WithFilter("-[*Tests*]*")
//        .ExcludeByAttribute("*.ExcludeFromCodeCoverage*")
//        .ExcludeByFile("*/*Designer.cs"));

//    ReportGenerator(parameters.Paths.Directories.TestResultsDir.CombineWithFilePath("./OpenCover.xml"), parameters.Paths.Directories.TestResultsDir);

}).ReportError(exception =>
{
    // var apiApprovals = GetFiles("./**/ApiApprovalTests.*");
    // CopyFiles(apiApprovals, parameters.Paths.Directories.TestResultsDir);
});

Task("Upload-Test-Coverage")
    .WithCriteria(() => !parameters.IsLocalBuild)
    .IsDependentOn("Run-Unit-Tests")
    .Does(() =>
{
    // Resolve the API key.
//    var token = EnvironmentVariable("COVERALLS_TOKEN");
//    if (string.IsNullOrEmpty(token))
//    {
//        throw new Exception("The COVERALLS_TOKEN environment variable is not defined.");
//    }

//    CoverallsIo(parameters.Paths.Directories.TestResultsDir.CombineWithFilePath("./OpenCover.xml"), new CoverallsIoSettings()
//    {
//        RepoToken = token
//    });
});

Task("Copy-Files")
    .IsDependentOn("Run-Unit-Tests")
    .Does(() =>
{
    // GitLink
    //if(parameters.IsRunningOnWindows)
    //{
    //    Information("Updating PDB files using GitLink");
    //    GitLink(
    //        Context.Environment.WorkingDirectory.FullPath,
    //        new GitLinkSettings {
    //
    //            SolutionFileName = parameters.Solution.FullPath,
    //            ShaHash = parameters.Version.Sha
    //        });
    //}

    // Copy files from artifact sources to artifact directory
    foreach(var project in parameters.Paths.Files.Projects.Where(x => !x.AssemblyName.EndsWith("Tests"))) 
    {
        CleanDirectory(parameters.Paths.Directories.ArtifactsBin.Combine(project.AssemblyName));
        CopyFiles(project.GetBinaries(),
            parameters.Paths.Directories.ArtifactsBin.Combine(project.AssemblyName));
    }
    // Copy license
    CopyFileToDirectory("./LICENSE", parameters.Paths.Directories.ArtifactsBin);
});

Task("Zip-Files")
    .IsDependentOn("Copy-Files")
    .Does(() =>
{
    var files = GetFiles( parameters.Paths.Directories.ArtifactsBin + "/**/*" );
    Zip(parameters.Paths.Directories.ArtifactsBin, parameters.Paths.Files.ZipBinaries, files);

	
});

Task("Create-NuGet-Packages")
    .IsDependentOn("Copy-Files")
    .Does(() =>
{
    // Build nuget
    foreach(var project in parameters.Paths.Files.Projects) 
    {
        Information("Building nuget package: " + project.AssemblyName + " Version: " + parameters.Version.NuGet);
        DotNetCorePack(
            project.ProjectFile.FullPath,
            new DotNetCorePackSettings 
            {
                Configuration = parameters.Configuration,
                OutputDirectory = parameters.Paths.Directories.NugetRoot,
                NoBuild = true,
                Verbosity = parameters.IsLocalBuild ? DotNetCoreVerbosity.Quiet : DotNetCoreVerbosity.Normal,
                ArgumentCustomization = aggs => aggs
                    .Append(GetDotNetCoreArgsVersions(parameters.Version))
            }
        );

    }

});


Task("Publish-NuGet")
    .IsDependentOn("Create-NuGet-Packages")
    .WithCriteria(() => parameters.ShouldPublish)
    .Does(() =>
{
    // Resolve the API key.
    var apiKey = EnvironmentVariable("NUGET_API_KEY");
    if(string.IsNullOrEmpty(apiKey) && !parameters.ShouldPublishToArtifactory) {
        throw new InvalidOperationException("Could not resolve NuGet API key.");
    }

    if(parameters.ShouldPublishToArtifactory) {

        var username = parameters.Artifactory.UserName;
        var password = parameters.Artifactory.Password;

        if(string.IsNullOrEmpty(username) && parameters.IsLocalBuild)
        {
            Console.Write("Artifactory UserName: ");
            username = Console.ReadLine();
        }
        if(string.IsNullOrEmpty(password) && parameters.IsLocalBuild)
        {
            Console.Write("Artifactory Password: ");
            password = Console.ReadLine();
        }
        apiKey = string.Concat(username, ":", password);
    }

    // Resolve the API url.
    var apiUrl = EnvironmentVariable("NUGET_URL");
    if(string.IsNullOrEmpty(apiUrl)) {
        throw new InvalidOperationException("Could not resolve NuGet API url.");
    }

    foreach(var package in parameters.Packages.Nuget)
    {
		Information("Publish nuget: " + package.PackagePath);
        var packageDir = apiUrl;
        if(parameters.ShouldPublishToArtifactory)
            packageDir = string.Concat(apiUrl, "/", package.Id);

		// Push the package.
		NuGetPush(package.PackagePath, new NuGetPushSettings {
		  ApiKey = apiKey,
		  Source = packageDir
		});
    }
});

Task("Upload-AppVeyor-Artifacts")
    .IsDependentOn("Zip-Files")
    .IsDependentOn("Upload-Test-Coverage")
    .IsDependentOn("Create-NuGet-Packages")
    .WithCriteria(() => parameters.IsRunningOnAppVeyor)
    .Does(() =>
{
    AppVeyor.UploadArtifact(parameters.Paths.Files.ZipBinaries);

    foreach(var package in GetFiles(parameters.Paths.Directories.NugetRoot + "/*"))
    {
        AppVeyor.UploadArtifact(package);
    }
});

Task("Create-VSTS-Artifacts")
    .IsDependentOn("Zip-Files")
    .IsDependentOn("Upload-Test-Coverage")
    .IsDependentOn("Create-NuGet-Packages")
    .WithCriteria(() => parameters.IsRunningOnVSTS)
    .Does(context =>
{
    var commands = context.BuildSystem().TFBuild.Commands;

    commands.UploadArtifact("source", context.Environment.WorkingDirectory + "/", "source");

    commands.AddBuildTag(parameters.Version.Sha);
    commands.AddBuildTag(parameters.Version.SemVersion);
});

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Package")
  .IsDependentOn("Zip-Files")
  .IsDependentOn("Create-NuGet-Packages");

Task("Default")
  .IsDependentOn("Package");

Task("AppVeyor")
  .IsDependentOn("Upload-AppVeyor-Artifacts")
  .IsDependentOn("Publish-NuGet");

Task("VSTS")
  .IsDependentOn("Create-VSTS-Artifacts");
Task("VSTS-Publish")
  .IsDependentOn("Publish-Nuget");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(parameters.Target);
