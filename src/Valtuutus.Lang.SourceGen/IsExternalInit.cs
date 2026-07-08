#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    // Polyfill required to use C# 9 'init' accessors and records when targeting netstandard2.0,
    // which doesn't ship this marker type in its reference assemblies.
    internal static class IsExternalInit
    {
    }
}
#endif
