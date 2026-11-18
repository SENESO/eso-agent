namespace EsoAgent.Core;

/// <summary>
/// The product version, read from the assembly metadata stamped by the
/// single source in Directory.Build.props (item 45). Every surface —
/// agent status, fleet hello, inventory, CLI --version, server --version,
/// the app's About line — reports THIS value, so they can never disagree.
/// </summary>
public static class ProductInfo
{
    public static string Version
    {
        get
        {
            var v = typeof(ProductInfo).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
