using System.Reflection;

namespace TarkovOverlay;

internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Load()
    {
        if (_cached is not null) return _cached;
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s is not null) return _cached = new Icon(s);
            }
        }
        catch { /* fall through */ }
        return _cached = SystemIcons.Application;
    }
}
