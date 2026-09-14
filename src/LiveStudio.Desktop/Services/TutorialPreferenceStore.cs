namespace LiveStudio.Desktop.Services;

internal sealed class TutorialPreferenceStore(string? path = null)
{
    private readonly string path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveStudio", "tutorial-v1.txt");

    internal bool HasSeen()
    {
        try { return File.Exists(path) && File.ReadAllText(path) == "done"; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal bool Remember()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, "done");
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
