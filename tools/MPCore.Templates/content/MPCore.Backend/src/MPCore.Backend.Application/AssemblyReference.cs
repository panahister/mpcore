namespace MPCore.Backend.Application;

/// <summary>
/// Identifies this assembly to the host. Wolverine discovers message handlers only in the assemblies
/// the host names (see <c>Hosting/HandlerAssemblies.cs</c>); this reference is how this layer is named.
/// </summary>
public static class AssemblyReference
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyReference).Assembly;
}
