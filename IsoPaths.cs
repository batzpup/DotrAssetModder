namespace DotrAssetModder;


public static class IsoPaths
{
 
    public static string FileName(string isoPath)
    {
        int slash = isoPath.LastIndexOf('\\');
        string name = slash >= 0 ? isoPath[(slash + 1)..] : isoPath;
        int semi = name.LastIndexOf(';');
        return semi >= 0 ? name[..semi] : name;
    }

   
    public static string FileNameWithoutExtension(string isoPath)
    {
        string name = FileName(isoPath);
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    public static bool NameEquals(string isoPath, string fileName) =>
        string.Equals(FileName(isoPath), fileName, StringComparison.OrdinalIgnoreCase);
}

public static class FileDialogFilter
{
    public static string Of(string filterList)
    {
        if (OperatingSystem.IsWindows()) return filterList;
        var exts = filterList.Split(',');
        return string.Join(",", exts.Concat(exts.Select(e => e.ToUpperInvariant())).Distinct());
    }
}
