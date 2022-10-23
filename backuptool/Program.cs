using System;
using System.IO;
using System.Configuration;
using System.Collections;
using System.Runtime.InteropServices;
using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Linq;
using System.Text;

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

    class Archiver
    {
        private List<string> _filesArchived;
        private List<string> _filesBatch;
        private string outputPath;
        private StreamWriter output;
        private int filenamePos7zOutput;
        private ulong _batchSizeCurrent = 0;
        private string _archiveTemplate;
        private enum _reader7zState
        {
            LookingForFileListStart,
            ParsingFileList,
            SkipAll
        }
        private readonly Settings _settings;
        // WINAPI #####################################################################################################

        // WINAPI #####################################################################################################
        public Archiver()
        {
            _settings = Settings.getInstance();
            _filesArchived = new List<string>();
            _filesBatch = new List<string>();
        }
        public void Initialize(string templatePath)
        {
            outputPath = _settings.WorkingDirectory + "\\" + "output.txt";
            if (!Directory.Exists(_settings.DestinationPath)) Directory.CreateDirectory(_settings.DestinationPath);

            _archiveTemplate = templatePath;
            //Populate already archived files DB
            DirectoryInfo dir = new DirectoryInfo(_settings.DestinationPath);
            if (!dir.Exists) return;

            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                if ((file.Extension.Equals(".7z")) && (file.FullName.StartsWith(_archiveTemplate, StringComparison.InvariantCultureIgnoreCase)))
                {
                    GetArchiveInfo(file.FullName);
                }
            }
        }
        private void GetArchiveInfo(string archivePath)
        {
            bool isBadFile = false;
            Process zip7 = new Process();
            zip7.StartInfo.FileName = _settings.Path7z;
            zip7.StartInfo.Arguments = String.Format("-sccUTF-8 -scsUTF-8 l \"{0}\"", archivePath);//Ради иероглифов, длинных тире и прочей диакритики
            zip7.StartInfo.UseShellExecute = false;
            zip7.StartInfo.RedirectStandardOutput = true;
            zip7.StartInfo.CreateNoWindow = true;
            zip7.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            zip7.Start();

            _reader7zState parserState = _reader7zState.LookingForFileListStart;
            string line;
            string lineArchived;

            while (!zip7.StandardOutput.EndOfStream)
            {
                if (parserState == _reader7zState.SkipAll)
                {
                    zip7.StandardOutput.ReadLine();
                    continue;
                }
                if (parserState == _reader7zState.ParsingFileList)
                {
                    line = zip7.StandardOutput.ReadLine();
                    if (line.Contains("-------------------")) { parserState = _reader7zState.SkipAll; }
                    else
                    {
                        if (line[filenamePos7zOutput + 1] == ':') filenamePos7zOutput += 3;//Если 7z начинает возвращать путь файла с корнем типа C:\\
                        lineArchived = line.Substring(filenamePos7zOutput).ToLower() + " " + line.Substring(0, 19);//Полный путь с датой через пробел
                        _filesArchived.Add(lineArchived);
                    }
                    continue;
                }
                if (parserState == _reader7zState.LookingForFileListStart)
                {
                    line = zip7.StandardOutput.ReadLine();
                    if (line.Contains("Date      Time    Attr"))
                    {
                        filenamePos7zOutput = line.IndexOf("Name");
                        parserState = _reader7zState.ParsingFileList;
                        zip7.StandardOutput.ReadLine();
                    }
                    if (line.Contains("ERROR"))
                    {
                        isBadFile = true;
                        parserState = _reader7zState.SkipAll;
                        Console.WriteLine("7z got error while reading info from " + archivePath + "! File will be deleted");
                    }
                    continue;
                }
            }
            zip7.WaitForExit();
            if ((isBadFile) || (zip7.ExitCode != 0)) File.Delete(archivePath);
        }
        public bool IsArchivedAlready(string filepath, System.Runtime.InteropServices.ComTypes.FILETIME lastwrite)
        {
            //Перевод времени
            //Хз как 7z считает время файла, лень смотреть исходник
            //file.LastWriteTime.IsDaylightSavingTime - показания расходятся с 7z в архиве!

            ulong hiTime = (ulong)lastwrite.dwHighDateTime;
            uint loTime = (uint)lastwrite.dwLowDateTime;
            long filetime = (long)((hiTime << 32) + loTime);

            DateTime dtLastWrite = DateTime.FromFileTime(filetime);

            string filerec0 = filepath.Substring(3).ToLower() + " " + dtLastWrite.ToString("yyyy-MM-dd HH:mm:ss");
            string filerec1 = filepath.Substring(3).ToLower() + " " + dtLastWrite.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");

            foreach (string record in _filesArchived)
            {
                if (filerec0.Equals(record) || (filerec1.Equals(record))) return true;
            }
            return false;
        }
        private string FindNextAvailableArchiveName()
        {
            long counter = 0;
            bool success = false;
            while (!success)
            {
                string newname = _archiveTemplate + String.Format("{0:D4}.7z", counter++);
                if (!File.Exists(newname))
                {
                    success = true;
                    return newname;
                }
            }
            return String.Format("You should not reach here!");
        }
        private void Call7z()
        {
            if (_filesBatch.Count == 0) return;
            if (File.Exists(outputPath)) File.Delete(outputPath);

            output = new StreamWriter(outputPath);
            foreach (string rec in _filesBatch) output.WriteLine(rec);
            output.Close();

            _filesBatch.Clear();
            _batchSizeCurrent = 0;

            string archivename = FindNextAvailableArchiveName();

            Process zip7 = new Process();
            zip7.StartInfo.FileName = _settings.Path7z;
            string resultingArgs = String.Format(_settings.Args7z, archivename, outputPath);
            if (!zip7.StartInfo.Arguments.Contains("-mmt=")) { resultingArgs += String.Format(" -mmt={0}", Environment.ProcessorCount > 1 ? Environment.ProcessorCount - 1 : 1); }

            zip7.StartInfo.Arguments = resultingArgs;

            //Должно скрывать окно
            //Но почему-то меняет exit code процесса 7z, что не приемлимо
            //zip7.StartInfo.UseShellExecute = false;
            //zip7.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            //zip7.StartInfo.CreateNoWindow = true;

            zip7.Start();
            zip7.PriorityClass = ProcessPriorityClass.Idle;
            zip7.WaitForExit();

            File.Delete(outputPath);

            if ((zip7.ExitCode == 0) || (zip7.ExitCode == 1)) return;

            Console.WriteLine($"ERROR: 7z returned {zip7.ExitCode}");
            System.Environment.Exit(zip7.ExitCode);
        }
        public void AddToBatch(string filepath, ulong size)
        {
            _filesBatch.Add(filepath);
            _batchSizeCurrent += size;
            if (_batchSizeCurrent >= _settings.BatchSizeMax)
            {
                Call7z();
            }
        }
        public void Finnalize()
        {
            Call7z();
        }
    }
    class Settings //singleton
    {
        private static Settings instance;

        public readonly bool verbose;
        public readonly List<string> BlackList;
        public readonly bool FixLongNames;
        public readonly string DestinationPath;
        public readonly ulong BatchSizeMax;
        public readonly string Additional7zArgs;
        public readonly string Args7z;
        public readonly string Path7z;
        public readonly Backupper.BackupMode WorkMode;
        public readonly string SpecificPath;
        public readonly string WorkingDirectory;
        public bool IgnoreProfileDir;
        private Settings()
        {
            string[] args = Environment.GetCommandLineArgs();

            verbose = (bool)Properties.Settings.Default["Verbose"];

            //Получаем значения по-умолчанию из .config
            //Blacklist
            BlackList = new List<string>();

            var stringCollection = Properties.Settings.Default["BlackList"] as StringCollection;
            foreach (string s in stringCollection) BlackList.Add(s.ToLower());

            //blacklist.Add(((string)Properties.Settings.Default["destination"]).ToLower());

            //FixLongNames
            FixLongNames = (bool)Properties.Settings.Default["FixLongNames"];

            //DestinationPath
            DestinationPath = (string)Properties.Settings.Default["DestinationPath"];

            //BatchSizeMax
            BatchSizeMax = (ulong)Properties.Settings.Default["BatchSizeMax"];

            //Additional7zArgs
            Additional7zArgs = (string)Properties.Settings.Default["Additional7zArgs"];
            string _base7zArgs = "a \"{0}\" -i@\"{1}\" ";
            Args7z = _base7zArgs + Additional7zArgs;

            //Path7z
            Path7z = (string)Properties.Settings.Default["Path7z"];

            //WorkMode
            string workmodeRaw = (string)Properties.Settings.Default["WorkMode"];
            SpecificPath = "";
            switch (workmodeRaw.ToLower())
            {
                case "both":
                    WorkMode = Backupper.BackupMode.Both;
                    break;
                case "profiles":
                    WorkMode = Backupper.BackupMode.OnlyProfiles;
                    break;
                case "drives":
                    WorkMode = Backupper.BackupMode.OnlyDrives;
                    break;
                default:
                    WorkMode = Backupper.BackupMode.SpecificPath;
                    SpecificPath = workmodeRaw;//nonlowercased
                    break;
            }
            //Другие настройки
            WorkingDirectory = Directory.GetCurrentDirectory().ToLower();
            IgnoreProfileDir = false;

            //Парсим аргументы и меняем настройки
            int arg_pos = 1;
            while (arg_pos < args.Length)
            {
                string arg = args[arg_pos];
                switch (arg.ToLower())
                {
                    case "-h":
                    case "-help":
                        Console.WriteLine("**** SMO backup tool ****");
                        Console.WriteLine("");
                        Console.WriteLine("Программа архивирует пользовательские профили и жесткие диски компьютера в 7z архивы");
                        Console.WriteLine(" и передает их по указанному пути, игнорирует файлы, уже содержащиеся в архивах (проверка по пути и дате изменения)");
                        Console.WriteLine("");
                        Console.WriteLine("Поддерживаемые аргументы :\n");
                        Console.WriteLine("-h -help\nВыводит данную справку и выходит из программы\n");
                        Console.WriteLine($" -d -DestinationPath {DestinationPath}\nПуть до целевой папки, куда сохранять архивы\n");
                        Console.WriteLine($" -FixLongNames {FixLongNames}\nПробовать исправлять длинные(>127 символов) имена файлов\nПереименовывает файл до короткого и кладет оригинальное имя в файл .meta рядом с коротким файлом\n");
                        Console.WriteLine($" -bs -BatchSizeMax {BatchSizeMax}\nРазмер файлов в пакете для упаковки в отдельный архив\nНапример -BatchSizeMax 100M\n         -BatchSizeMax 10G\n");
                        Console.WriteLine($" -Path7z {Path7z}\nПуть до 7z.exe\n");
                        Console.WriteLine($" -Additional7zArgs {Additional7zArgs}\nДополнительные параметры коммандной строки 7z\n");
                        Console.WriteLine($" -b -Backup [Both | Profiles | Drives | %path%]\nБекапить всё, только профили, только диски(без папки Users) или указанный путь");
                        Console.WriteLine($" -i {IgnoreProfileDir}\nИгнорировать :\\Users");
                        Console.WriteLine($" -v -verbose {verbose}\nПечатать лог");
                        System.Environment.Exit(0);
                        break;
                    case "-i":
                        IgnoreProfileDir = true;
                        break;
                    case "-bs":
                    case "-batchsizemax":
                        if (arg_pos + 1 == args.Length)
                        {
                            Console.WriteLine($"Expected argument after {arg}, but got end of arguments");
                        }
                        string _sizeRaw = args[arg_pos + 1];
                        int len = _sizeRaw.Length;
                        int multiplier = 1;
                        switch (_sizeRaw[len - 1])
                        {
                            case 'm':
                            case 'M':
                                multiplier = 1024 * 1024;
                                _sizeRaw = _sizeRaw.Substring(0, len - 1);
                                break;
                            case 'g':
                            case 'G':
                                multiplier = 1024 * 1024 * 1024;
                                _sizeRaw = _sizeRaw.Substring(0, len - 1);
                                break;
                            default:
                                break;
                        }
                        BatchSizeMax = ulong.Parse(_sizeRaw) * (ulong)multiplier;
                        arg_pos++;
                        break;
                    case "-d":
                    case "-destinationpath":
                        if (arg_pos + 1 == args.Length)
                        {
                            Console.WriteLine($"Expected argument after {arg}, but got end of arguments");
                        }
                        DestinationPath = args[arg_pos + 1];
                        arg_pos++;
                        break;
                    case "-fixlongpath":
                        if (arg_pos + 1 == args.Length)
                        {
                            Console.WriteLine($"Expected argument after {arg}, but got end of arguments");
                        }
                        FixLongNames = bool.Parse(args[arg_pos + 1]);
                        arg_pos++;
                        break;
                    case "-path7z":
                        if (arg_pos + 1 == args.Length)
                        {
                            Console.WriteLine($"Expected argument after {arg}, but got end of arguments");
                        }
                        Path7z = args[arg_pos + 1];
                        arg_pos++;
                        break;
                    case "-additional7zargs":
                        if (arg_pos + 1 == args.Length)
                        {
                            Console.WriteLine($"Expected argument after {arg}, but got end of arguments");
                        }
                        Additional7zArgs = args[arg_pos + 1];
                        arg_pos++;
                        break;
                    case "-b":
                    case "-backup":
                        if (arg_pos + 1 == args.Length)
                        {
                            Console.WriteLine($"Expected argument after {arg}, but got end of arguments");
                        }
                        string value = args[arg_pos + 1].ToLower();
                        switch (value)
                        {
                            case "both":
                                WorkMode = Backupper.BackupMode.Both;
                                break;
                            case "profiles":
                                WorkMode = Backupper.BackupMode.OnlyProfiles;
                                break;
                            case "drives":
                                WorkMode = Backupper.BackupMode.OnlyDrives;
                                break;
                            default:
                                WorkMode = Backupper.BackupMode.SpecificPath;
                                SpecificPath = args[arg_pos + 1];//nonlowercased
                                break;
                        }
                        arg_pos++;
                        break;
                    case "-v":
                    case "-verbose":
                        verbose = true;
                        break;
                    default:
                        Console.WriteLine($"Unknown argument {arg}\nTerminating!");
                        System.Environment.Exit(-1);
                        break;
                }
                arg_pos++;
            }

            //Обновляем BlackList, чтобы не бекапить сами архивы и текущую рабочую папку
            BlackList.Add(DestinationPath.ToLower());
            BlackList.Add(Directory.GetCurrentDirectory().ToLower());
        }
        public static Settings getInstance()
        {
            if (instance == null)
                instance = new Settings();

            return instance;
        }
    }
    class Validator
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
    class FileTreeWalker
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

    class Backupper
    {
        private readonly Settings _settings = Settings.getInstance();
        public enum BackupMode
        {
            Both,
            OnlyProfiles,
            OnlyDrives,
            SpecificPath
        }
        public List<UserProfile> GetValidProfiles(DateTime validDate)
        {
            string validDateStr = validDate.ToString("yyyyMMdd000000.000000+000");
            List<UserProfile> result = new List<UserProfile>();

            ManagementObjectCollection profiles;
            ManagementObjectSearcher queryProfiles = new ManagementObjectSearcher("root\\cimv2", $"SELECT * FROM Win32_UserProfile WHERE Special = 'False' AND LastUseTime > '{validDateStr}'");
            profiles = queryProfiles.Get();

            Console.WriteLine("Got Profiles from WMI:");
            foreach (ManagementObject profile in profiles)
            {
                Console.WriteLine($" Path={profile["LocalPath"]} LastUse={profile["LastUseTime"]} SID={profile["SID"]}");
            }

            Domain dom = null;
            bool domainless = true;
            try
            {
                dom = Domain.GetComputerDomain();
                domainless = false;
                Console.WriteLine($"Detected domain : {dom.Name}");
            }
            catch (ActiveDirectoryObjectNotFoundException)
            {
                domainless = true;
            }

            if (domainless)
            {
                foreach (ManagementObject profile in profiles)
                {
                    ManagementObjectSearcher queryAccount = new ManagementObjectSearcher("root\\cimv2", $"SELECT Domain, Name, FullName FROM Win32_UserAccount WHERE SID = '{profile["SID"]}' AND LocalAccount = 'True'");
                    ManagementObject account = queryAccount.Get().OfType<ManagementObject>().FirstOrDefault();
                    result.Add(new UserProfile(account["Domain"].ToString(),
                                                    account["Name"].ToString(),
                                                    account["FullName"].ToString(),
                                                    profile["LocalPath"].ToString(),
                                                    profile["LastUseTime"].ToString()));
                }
            }
            else
            {
                foreach (ManagementObject profile in profiles)
                {
                    Console.WriteLine($"Looking for profile with path={profile["LocalPath"]} and SID={profile["SID"]} in domains");

                    UserProfile temp = null;

                    foreach (Domain domname in dom.Forest.Domains)
                    {
                        if (temp is null)
                        {
                            Console.WriteLine($"\tLooking in {domname.Name}");
                            var root = new DirectoryEntry("LDAP://" + domname.Name);
                            var searcher = new DirectorySearcher(root);
                            searcher.PropertiesToLoad.Add("displayName");
                            searcher.PropertiesToLoad.Add("lastLogon");
                            searcher.PropertiesToLoad.Add("userPrincipalName");
                            searcher.Filter = $"(&(objectCategory=person)(objectClass=user)(ObjectSid={profile["SID"]}))";
                            var q = searcher.FindOne();

                            if (!(q is null))
                            {
                                Console.WriteLine($"\tRecord found! Name={q.Properties["displayName"][0].ToString()} PrincipalName={q.Properties["userPrincipalName"][0].ToString()}");
                                temp = new UserProfile(dom.Name,
                                                        q.Properties["userPrincipalName"][0].ToString(),
                                                        q.Properties["displayName"][0].ToString(),
                                                        profile["LocalPath"].ToString(),
                                                        profile["LastUseTime"].ToString());
                            }
                        }
                    }

                    if (!(temp is null)) { result.Add(temp); }
                    else Console.WriteLine("\tNot found!");
                }
            }

            return result;
        }
        public void DoBackup()
        {
            switch (_settings.WorkMode)
            {
                case BackupMode.OnlyProfiles:
                    BackupProfiles();
                    break;
                case BackupMode.OnlyDrives:
                    BackupDrives();
                    break;
                case BackupMode.Both:
                    BackupProfiles();
                    BackupDrives();
                    break;
                case BackupMode.SpecificPath:
                    BackupCustom();
                    break;
                default: break;
            }
        }

        private void BackupDrives()
        {
            DriveInfo[] allDrives = DriveInfo.GetDrives();
            foreach (DriveInfo drive in allDrives)
            {
                if ((drive.IsReady) && (drive.DriveType == DriveType.Fixed)) BackupSingleDrive(drive.RootDirectory);
            }
        }
        private void BackupSingleDrive(DirectoryInfo root)
        {
            var _arc = new Archiver();
            _arc.Initialize(_settings.DestinationPath + "\\" + "drive_" + root.FullName.Substring(0, 1) + "_");
            var _val = new Validator(_arc);
            var _wtf = new FileTreeWalker(_val, _arc);

            _wtf.Walk("\\\\?\\" + root.FullName.TrimEnd('\\'));

            _arc.Finnalize();
        }
        private void BackupProfiles()
        {
            foreach (UserProfile profile in GetValidProfiles(DateTime.Now.AddYears(-1)))
            {
                BackupSingleProfile(profile);
            }
        }
        private void BackupSingleProfile(UserProfile profile)
        {
            string metapath = _settings.DestinationPath + "\\profile_" + profile.name.ToLower() + ".meta";

            StreamWriter file = new StreamWriter(metapath);
            file.WriteLine($"Domain: {profile.domain}");
            file.WriteLine($"Name: {profile.name}");
            file.WriteLine($"FullName: {profile.fullname}");
            file.WriteLine($"ProfilePath: {profile.profilepath}");
            file.WriteLine($"LastUsetime: {profile.lastusetime}");
            file.Close();

            _settings.IgnoreProfileDir = false;

            var _arc = new Archiver();
            _arc.Initialize(_settings.DestinationPath + "\\" + "profile_" + profile.name.ToLower() + "_");
            var _val = new Validator(_arc);
            var _wtf = new FileTreeWalker(_val, _arc);

            _wtf.Walk("\\\\?\\" + profile.profilepath.TrimEnd('\\'));

            _arc.Finnalize();
        }
        private void BackupCustom()
        {
            string ident = String.Format("{0:X}", _settings.SpecificPath.GetHashCode());
            DirectoryInfo di = new DirectoryInfo(_settings.SpecificPath);

            bool isRoot = di.FullName.Equals(di.Root.FullName);
            string basePath = isRoot ? _settings.DestinationPath + "\\" + "archive_drive_" + di.Name.Substring(0, 1).ToLower() + "_" + ident :
                                       _settings.DestinationPath + "\\" + "archive_" + di.Name.ToLower() + "_" + ident;


            string metapath = basePath + ".meta";

            StreamWriter file = new StreamWriter(metapath);
            file.WriteLine(_settings.SpecificPath);
            file.Close();

            var _arc = new Archiver();
            _arc.Initialize(basePath + "_");

            var _val = new Validator(_arc);
            var _wtf = new FileTreeWalker(_val, _arc);

            _wtf.Walk("\\\\?\\" + di.FullName.TrimEnd('\\'));

            _arc.Finnalize();
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
