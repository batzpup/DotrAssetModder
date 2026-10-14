namespace DotrAssetModder;


public static class IsoPaths
{
 
    public static string FileName(string isoPath)
    {
        int lastBackslashIndex = isoPath.LastIndexOf('\\');
        string fileName = lastBackslashIndex >= 0 ? isoPath[(lastBackslashIndex + 1)..] : isoPath;
        int versionSeparatorIndex = fileName.LastIndexOf(';');
        return versionSeparatorIndex >= 0 ? fileName[..versionSeparatorIndex] : fileName;
    }

   
    public static string FileNameWithoutExtension(string isoPath)
    {
        string fileName = FileName(isoPath);
        int extensionDotIndex = fileName.LastIndexOf('.');
        return extensionDotIndex > 0 ? fileName[..extensionDotIndex] : fileName;
    }
}

public static class FileDialogFilter
{
    public static string Of(string filterList)
    {
        if (OperatingSystem.IsWindows()) return filterList;
        var extensions = filterList.Split(',');
        return string.Join(",", extensions.Concat(extensions.Select(extension => extension.ToUpperInvariant())).Distinct());
    }
}
