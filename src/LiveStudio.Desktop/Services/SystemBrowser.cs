using System.Diagnostics;

namespace LiveStudio.Desktop.Services;

public interface ISystemBrowser
{
    void Open(Uri uri);
}

public sealed class SystemBrowser : ISystemBrowser
{
    public void Open(Uri uri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }
}
