using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);

#if NET11_0_OR_GREATER
// BDN 0.15.8 doesn't recognize net11.0 runtime; in-process toolchain runs on current runtime.
var config = ManualConfig.CreateEmpty()
    .WithSummaryStyle(BenchmarkDotNet.Reports.SummaryStyle.Default)
    .AddColumnProvider(DefaultColumnProviders.Instance)
    .AddLogger(ConsoleLogger.Default)
    .AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
switcher.Run(args, config);
#else
switcher.Run(args);
#endif
