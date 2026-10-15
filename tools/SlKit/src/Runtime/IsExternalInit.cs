// The C# compiler needs this marker type to accept `init` accessors and `record` types. The server's
// BCL (mscorlib, System.Core, netstandard from SCPSL_Data/Managed) does not define it, so the
// compiler reports CS0518 without a copy. Verified by searching those assemblies: no match.
//
// Guards, so the copy cannot clash:
//  - NET5_0_OR_GREATER: the real type exists, do not shadow it.
//  - SLKIT_NO_ISEXTERNALINIT: define it in your project when you source-include SlKit and already
//    have your own IsExternalInit (or another shared library exposes one); two public copies in two
//    referenced assemblies make `init` ambiguous (CS0433).
#if !NET5_0_OR_GREATER && !SLKIT_NO_ISEXTERNALINIT
namespace System.Runtime.CompilerServices;

/// <summary>
/// Compiler marker that enables <c>init</c>-only setters and records on a runtime that lacks it.
/// Not meant to be used directly. Thread affinity: not applicable (no members).
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class IsExternalInit
{
}
#endif
