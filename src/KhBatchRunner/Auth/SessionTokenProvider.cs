using System.Reflection;

namespace KhBatchRunner.Auth;

/// <summary>
/// Borrows the signed-in desktop Revit session token via the undocumented
/// <c>SSONET.dll</c> (<c>Autodesk.Revit.AdWebServicesBase.GetInstance()
/// .GetOAuth2AccessToken()</c>), re-fetched per call. Reflection-based so a
/// missing/renamed assembly degrades gracefully instead of failing to load the add-in.
///
/// This does NOT require any document (cloud or local) to be open — it reads the
/// interactive account session, exactly like the team's IronPython crawler and other
/// Forma-integrated add-ins.
///
/// RISK: SSONET is undocumented and may change between Revit releases — this is the
/// single place that touches it. Only the Forma browser/crawler needs it.
/// </summary>
public sealed class SessionTokenProvider : ITokenProvider
{
    /// <summary>
    /// Reason the last <see cref="GetAccessToken"/> returned empty, for UI diagnostics.
    /// Null after a successful fetch.
    /// </summary>
    public string? LastError { get; private set; }

    public string GetAccessToken()
    {
        LastError = null;
        try
        {
            Assembly ssonet = LoadSsonet();

            // SSONET.dll exposes the type under the Autodesk.Revit namespace
            // (NOT Autodesk.SSO — that was the bug that produced an always-empty token).
            Type? baseType = ssonet.GetType("Autodesk.Revit.AdWebServicesBase");
            if (baseType is null)
            {
                LastError = "SSONET Autodesk.Revit.AdWebServicesBase type not found " +
                            "(Revit version change?).";
                return "";
            }

            object? instance = baseType
                .GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static)?
                .Invoke(null, null);
            if (instance is null)
            {
                LastError = "AdWebServicesBase.GetInstance() returned null.";
                return "";
            }

            // Require an interactive sign-in; otherwise the token would be blank/expired.
            MethodInfo? isLoggedIn = instance.GetType().GetMethod("IsLoggedIn");
            if (isLoggedIn?.Invoke(instance, null) is bool loggedIn && !loggedIn)
            {
                LastError = "Not signed in to Autodesk in Revit " +
                            "(sign in to your Forma account, then retry).";
                return "";
            }

            object? token = instance.GetType()
                .GetMethod("GetOAuth2AccessToken")?
                .Invoke(instance, null);
            string result = token as string ?? "";
            if (string.IsNullOrEmpty(result))
                LastError = "Signed in, but Revit returned an empty OAuth token.";
            return result;
        }
        catch (Exception ex)
        {
            // Surface the real cause (unwrap reflection's TargetInvocationException).
            LastError = (ex.InnerException ?? ex).Message;
            return "";
        }
    }

    /// <summary>
    /// SSONET is already loaded in-process by Revit, so prefer the loaded instance;
    /// fall back to <see cref="Assembly.Load(string)"/> by simple name.
    /// </summary>
    private static Assembly LoadSsonet()
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(a.GetName().Name, "SSONET", StringComparison.OrdinalIgnoreCase))
                return a;
        }
        return Assembly.Load("SSONET");
    }
}
