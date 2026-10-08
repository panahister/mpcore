namespace MPCore.Application.Modules;

/// <summary>
/// Declares, on an interface a module publishes in its Contracts project, why another module may change this
/// module's data through it, inside the caller's transaction. Between modules the default is a message; a
/// writing call is a deliberate exception for two modules that must change together and stay in one
/// deployment (ADR-012, section 7). A generated backend's tests fail on a writing Contracts interface without it.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class CrossModuleWriteAttribute : Attribute
{
    /// <summary>Declares the reason.</summary>
    /// <param name="reason">Why the two modules must change together; not empty.</param>
    public CrossModuleWriteAttribute(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    /// <summary>Gets why the two modules must change together.</summary>
    public string Reason { get; }
}
