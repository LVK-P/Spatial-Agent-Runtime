namespace SpatialAgent.Core
{
    /// <summary>
    /// Converts host datastore URIs to paths suitable for System.IO without
    /// changing non-file datastore identities.
    /// </summary>
    public static class FileSystemDataSourcePath
    {
        public static string Normalize(Uri? dataSource)
        {
            if (dataSource == null)
                return "";

            if (!dataSource.IsFile)
                return dataSource.ToString();

            return Uri.UnescapeDataString(dataSource.LocalPath);
        }
    }
}
