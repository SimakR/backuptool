using System.IO;
using System.Runtime.InteropServices;

namespace backuptool
{
    internal class Validator
    {
        // WINAPI #####################################################################################################
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetFileAttributesExW(string lpFileName, GET_FILEEX_INFO_LEVELS fInfoLevelId, out WIN32_FIND_DATAW lpFileInformation);
        // WINAPI #####################################################################################################
        private Settings _settings = Settings.getInstance();
        private Archiver _archiver;
        public Validator(Archiver archiver)
        {
            _archiver = archiver;
        }
        public Decision Validate(string directory, string entryname)
        {
            string fullname = directory + "\\" + entryname;
            //Особый случай для корня диска
            if (fullname.Length <= 7) return Decision.ScanDirectory;

            WIN32_FIND_DATAW data;
            GetFileAttributesExW(fullname, GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard, out data);

            //Пропускаем скрытые, системные и симлинки
            if ((data.dwFileAttributes & (uint)FileAttributes.Hidden) != 0) return Decision.Skip;
            if ((data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0) return Decision.Skip;
            if ((data.dwFileAttributes & (uint)FileAttributes.System) != 0) return Decision.Skip;

            //Игнор директории профилей
            if (_settings.IgnoreProfileDir & (fullname.Contains("C:\\Users"))) return Decision.Skip;

            //Черный список
            foreach (string word in _settings.BlackList)
            {
                if (fullname.ToLower().Contains(word)) return Decision.Skip;
            }

            //Проверка на читаемый файл
            try
            {
                using (FileStream stream = File.Open(fullname, FileMode.Open, FileAccess.Read))
                {
                }
            }
            catch (IOException)
            {
                return Decision.Skip;
            }

            //Переименование под ограничения ext4
            if ((entryname.Length >= 127) & _settings.FixLongNames) return Decision.Rename;


            if ((data.dwFileAttributes & (uint)FileAttributes.Directory) != 0)
            {
                return Decision.ScanDirectory;
            }
            else
            {
                //Архивирован?
                if (!_archiver.IsArchivedAlready(fullname.Substring(4), data.ftLastWriteTime)) return Decision.ArchiveFile;
            }

            return Decision.Skip;
        }
    }
}
