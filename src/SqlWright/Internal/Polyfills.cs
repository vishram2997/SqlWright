#if !NET6_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// The C# compiler recognises interpolated string handlers by this attribute's name, so an internal copy
    /// lets the netstandard2.0 build expose <see cref="SqlWright.Sql"/> as a handler to C# 10+ callers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }
}
#endif
