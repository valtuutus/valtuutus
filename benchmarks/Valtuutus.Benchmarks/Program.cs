using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);

// All [Benchmark] methods are read-only (no writes/inserts/updates), so sharing one
// process/container/GlobalSetup across every benchmark case in a class is safe. Without
// this, BDN's default out-of-process toolchain builds one child process per benchmark
// case, re-running GlobalSetup (container start + migrate + seed) once per case instead
// of once per class.
var config = ManualConfig.CreateEmpty()
    .WithSummaryStyle(BenchmarkDotNet.Reports.SummaryStyle.Default)
    .AddColumnProvider(DefaultColumnProviders.Instance)
    .AddLogger(ConsoleLogger.Default)
    .AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
switcher.Run(args, config);
