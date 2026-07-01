using System.Security.Principal;

namespace Setup.Core.Utils;

/// <summary>Elevation helpers.</summary>
public static class AdminHelper
{
    /// <summary>True when the current process is running elevated (Administrators).</summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
