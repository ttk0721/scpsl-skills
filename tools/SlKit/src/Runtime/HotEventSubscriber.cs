namespace SlKit;

/// <summary>One handler subscribed to a hot LabAPI event, as found by <see cref="HotEventAudit"/>.</summary>
/// <remarks>Plain immutable data (strings only), so it is safe to hand to any thread.</remarks>
public sealed class HotEventSubscriber
{
    internal HotEventSubscriber(
        string eventName,
        string method,
        string declaringType,
        string ownerType,
        string assembly,
        bool isCompilerGenerated)
    {
        EventName = eventName;
        Method = method;
        DeclaringType = declaringType;
        OwnerType = ownerType;
        Assembly = assembly;
        IsCompilerGenerated = isCompilerGenerated;
    }

    /// <summary>Catalog name of the event, for example <c>PlayerEvents.ReceivingVoiceMessage</c>.</summary>
    public string EventName { get; }

    /// <summary>Name of the subscribed method. Lambdas show up as <c>&lt;Enable&gt;b__0_0</c>.</summary>
    public string Method { get; }

    /// <summary>Full name of the type that declares <see cref="Method"/>; empty for dynamic methods.</summary>
    public string DeclaringType { get; }

    /// <summary>
    /// The first enclosing type that is not compiler-generated. For a lambda this is the class
    /// you wrote it in, not the hidden <c>&lt;&gt;c</c> closure class.
    /// </summary>
    public string OwnerType { get; }

    /// <summary>Simple name and version of the assembly that contains the handler, for example <c>MyPlugin 1.2.0.0</c>.</summary>
    public string Assembly { get; }

    /// <summary>True when the handler is a lambda or anonymous method (its name or type is compiler-generated).</summary>
    public bool IsCompilerGenerated { get; }

    /// <summary>One-line description: <c>OwnerType.Method [Assembly]</c>.</summary>
    public override string ToString()
    {
        string owner = OwnerType.Length > 0 ? OwnerType + "." : string.Empty;
        return owner + Method + (IsCompilerGenerated ? " (lambda)" : string.Empty) + " [" + Assembly + "]";
    }
}
