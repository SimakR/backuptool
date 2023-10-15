using System;
using System.Runtime.InteropServices;


namespace backuptool
{
    //WINAPI
    //Для того, чтобы корректно работать с длинными путями, нужно
    // 1. Использовать юникод-версии функций
    // 2. Работать через префикс \\?\
    // Для того чтобы нативно работать с п2, нужен net>4.7, а мы ограничены .net=3.5 (win7)

    // WINAPI #####################################################################################################
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
        public uint dwFileType;
        public uint dwCreatorType;
        public uint wFinderFlags;
    }
    enum GET_FILEEX_INFO_LEVELS
    {
        GetFileExInfoStandard,
        GetFileExMaxInfoLevel
    }
    [Flags]
    enum MoveFileFlags
    {
        MOVEFILE_REPLACE_EXISTING = 0x00000001,
        MOVEFILE_COPY_ALLOWED = 0x00000002,
        MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004,
        MOVEFILE_WRITE_THROUGH = 0x00000008,
        MOVEFILE_CREATE_HARDLINK = 0x00000010,
        MOVEFILE_FAIL_IF_NOT_TRACKABLE = 0x00000020
    }
    // WINAPI #####################################################################################################

    enum Decision
    {
        Skip,
        ArchiveFile,
        ScanDirectory,
        Rename
    }
    class UserProfile
    {
        public string domain;
        public string name;
        public string fullname;
        public string profilepath;
        public string lastusetime;
        public UserProfile(string _domain, string _name, string _fullname, string _profilepath, string _lastusetime)
        {
            domain = _domain;
            name = _name;
            fullname = _fullname;
            profilepath = _profilepath;
            lastusetime = _lastusetime;
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Backupper backupper = new Backupper();
            backupper.DoBackup();
        }
    }
}
