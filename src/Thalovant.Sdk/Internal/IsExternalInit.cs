#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Lets netstandard2.1 compile <c>init</c> accessors; .NET 5 and later ship
    /// this type themselves.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
#endif
