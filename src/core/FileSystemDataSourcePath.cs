namespace SpatialAgent.Core
{
    /// <summary>
    /// Converts host datastore URIs to paths suitable for System.IO without
    /// changing non-file datastore identities.
    /// </summary>
    /// <remarks>中文：将主机数据存储 URI 转换为适合 System.IO 的路径，而不更改非文件存储的身份。</remarks>
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
