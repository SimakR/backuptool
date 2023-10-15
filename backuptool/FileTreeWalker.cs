using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace backuptool
{
    internal class FileTreeWalker
    {
        // WINAPI #####################################################################################################
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATAW lpFindFileData);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool FindNextFileW(IntPtr hFindFile, ref WIN32_FIND_DATAW lpFindFileData);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool FindClose(ref IntPtr hFindFile);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileW(string lpExistingFileName, string lpNewFileName);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName,
                                                uint dwDesiredAccess,
                                                uint dwShareMode,
                                                uint lpSecurityAttributes,
                                                uint dwCreationDisposition,
                                                uint dwFlagsAndAttributes,
                                                uint hTemplateFile);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool WriteFile(IntPtr hFile, StringBuilder lpBuffer, uint nNumbersOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetFileAttributesExW(string lpFileName, GET_FILEEX_INFO_LEVELS fInfoLevelId, out WIN32_FIND_DATAW lpFileInformation);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint CREATE_NEW = 1;
        private const uint CREATE_ALWAYS = 2;
        private const uint OPEN_EXISTING = 3;
        // WINAPI #####################################################################################################

        private Settings _settings = Settings.getInstance();
        private Action<string> printlog;
        private readonly Validator _validator;
        private readonly Archiver _archiver;

        public FileTreeWalker(Validator validator, Archiver archiver)
        {
            _validator = validator;
            _archiver = archiver;
            printlog = message => { };
            if (_settings.verbose) printlog = message => { Console.WriteLine(message); };
        }
        private void RenameEntry(string directory, string entryname)
        {
            string oldname = entryname;

            string ext = "";
            string namenoext = entryname;
            string newname;

            int extpos = entryname.LastIndexOf('.');
            if (extpos != -1)
            {
                ext = entryname.Substring(extpos + 1);
                namenoext = entryname.Substring(0, extpos);
                newname = entryname.Substring(0, 100) + String.Format("_{0:X}", entryname.GetHashCode()) + "." + ((ext.Length > 10) ? ext.Substring(0, 10) :
                                                                                                                                                   ext);
            }
            else
            {
                newname = entryname.Substring(0, 100) + String.Format("_{0:X}", entryname.GetHashCode());
            }

            string metaname = entryname.Substring(0, 100) + String.Format("_{0:X}", entryname.GetHashCode()) + ".meta";

            if (!MoveFileW(directory + "\\" + oldname, directory + "\\" + newname))
            {
                printlog($"Failed to move from {oldname} to {newname}!");
            }
            else
            {
                printlog($"Renamed {oldname} to {newname}!");

                var hFile = CreateFileW(directory + "\\" + metaname, GENERIC_READ | GENERIC_WRITE, 0, 0, CREATE_ALWAYS, (uint)FileAttributes.Normal, 0);
                uint written;
                StringBuilder buffer = new StringBuilder(oldname);
                WriteFile(hFile, buffer, (uint)oldname.Length, out written, IntPtr.Zero);
                CloseHandle(hFile);

                ProcessEntry(directory, newname);
                ProcessEntry(directory, metaname);
            }
        }
        private void ProcessEntry(string directory, string entryname)
        {
            string fullname = directory + ((entryname.Length > 0) ? "\\" + entryname : "");

            var decision = _validator.Validate(directory, entryname);
            switch (decision)
            {
                case Decision.ArchiveFile:
                    printlog($"{fullname} => archive");

                    WIN32_FIND_DATAW data;
                    GetFileAttributesExW(fullname, GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard, out data);
                    ulong filesize = (ulong)((data.nFileSizeHigh << 32) + data.nFileSizeLow);
                    _archiver.AddToBatch(fullname.Substring(4), filesize);

                    break;
                case Decision.ScanDirectory:
                    printlog($"{fullname} => scan");

                    WIN32_FIND_DATAW finddata;

                    var hSearch = FindFirstFileW(fullname + "\\*", out finddata);
                    do
                    {
                        if (finddata.cFileName == ".") continue;
                        if (finddata.cFileName == "..") continue;

                        ProcessEntry(fullname, finddata.cFileName);

                    } while (FindNextFileW(hSearch, ref finddata) != false);
                    FindClose(ref hSearch);

                    break;
                case Decision.Rename:
                    printlog($"{fullname} => rename");
                    RenameEntry(directory, entryname);

                    break;
                case Decision.Skip:
                default:
                    printlog($"{fullname} => skip");

                    break;
            }
        }
        public void Walk(string path)
        {
            printlog($"Start archiving at {path}");

            ProcessEntry(path, "");

            printlog("All done!");
        }
    }
}
