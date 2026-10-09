using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MouseTrackerTabajara.Application;

/// <summary>Win32 metadata distinguishes Cloud Files placeholders from path redirection.
/// File.GetAttributes/LinkTarget alone can mask cloud attributes depending on process compatibility.
/// Microsoft documents FindFirstFile.dwReserved0 and CLOUD..CLOUD_F in MS-FSCC/WinNT.h.</summary>
internal static class ProjectReparseTags
{
    private const uint ReparseAttribute=0x400;
    private const uint NameSurrogate=0x20000000;
    private const uint CloudBase=0x9000001A;
    private const uint CloudVariantMask=0x0000F000;

    internal static bool IsAllowedCloudTag(uint tag)=>
        (tag&NameSurrogate)==0 && (tag&~CloudVariantMask)==CloudBase;

    internal static void RequireSafePathComponent(string path)
    {
        if(!OperatingSystem.IsWindows())
        {
            if(new FileInfo(path).LinkTarget is not null||(File.Exists(path)||Directory.Exists(path))&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Project paths through links or unknown reparse points are not supported.");
            return;
        }
        var data=Inspect(path);
        if(data is null)return;
        if((data.Value.Attributes&ReparseAttribute)==0)return;
        if(!IsAllowedCloudTag(data.Value.Tag))throw new IOException("Project paths through links or unknown reparse points are not supported.");
    }

    internal static (uint Attributes,uint Tag)? Inspect(string path)
    {
        var full=Path.GetFullPath(path);
        // FindFirstFile accepts extended Unicode paths; it does not accept a trailing separator.
        if(full.Length>Path.GetPathRoot(full)!.Length)full=full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        if(!full.StartsWith(@"\\?\",StringComparison.Ordinal))full=full.StartsWith(@"\\",StringComparison.Ordinal)?@"\\?\UNC\"+full[2..]:@"\\?\"+full;
        if(full.EndsWith(":\\",StringComparison.Ordinal))return null; // A drive root is not a pathname reparse component.
        var handle=FindFirstFileW(full,out var data);
        if(handle==new IntPtr(-1))
        {
            var error=Marshal.GetLastWin32Error();
            if(error is 2 or 3)return null; // Absent target/sidecar. Existing ancestors are checked separately.
            throw new IOException("Project path metadata is unavailable.",new Win32Exception(error));
        }
        try{return(data.Attributes,(data.Attributes&ReparseAttribute)!=0?data.Reserved0:0);}
        finally{FindClose(handle);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct FindData
    {
        public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;
        public uint SizeHigh,SizeLow,Reserved0,Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Filename;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=14)]public string Alternate;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern IntPtr FindFirstFileW(string path,out FindData data);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool FindClose(IntPtr handle);
}
