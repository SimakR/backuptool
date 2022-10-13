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
    class Validator
    {
        private Settings _settings;
        private Archiver _archiver;
        public Validator(Settings settings, Archiver archiver)
        {
            _settings = settings;
            _archiver = archiver;
        }
        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, MoveFileFlags dwFlags);
        private void CheckAndFixLength(ref FileSystemInfo entry)
        {
            const int maxPath = 127;//255 байт на имя, 2 байта на кирилицу, 255/2 = 127
            if (entry.Name.Length <= maxPath) return; 

            bool isDir = (entry.Attributes & FileAttributes.Directory) > 0;

            if (isDir)
            {
                DirectoryInfo di = (DirectoryInfo)entry;

                string oldname = di.FullName;
                string newname = di.Parent.FullName + "\\" + di.Name.Substring(0, 100) + String.Format("_{0:X}", di.Name.GetHashCode()) + ((di.Extension.Length > 10) ? di.Extension.Substring(0, 10) :
                                                                                                                                                                        di.Extension);
                string metapath = di.Parent.FullName + "\\" + di.Name.Substring(0, 100) + String.Format("_{0:X}", di.Name.GetHashCode()) + ".meta";

                try
                {
                    MoveFileEx("\\\\?\\" + oldname, "\\\\?\\" + newname, MoveFileFlags.MOVEFILE_REPLACE_EXISTING);
                }
                catch
                {
                    Console.WriteLine($"Cannot rename directory {oldname}");
                }

                try
                {
                    using (var meta = new StreamWriter(metapath))
                    {
                        meta.WriteLine(oldname);
                    }
                }
                catch { };

                entry = new DirectoryInfo(newname);
                _archiver.AddToBatch(new FileInfo(metapath));
            }
            else
            {
                FileInfo fi = (FileInfo)entry;

                string oldname = fi.FullName;
                string newname = fi.DirectoryName + "\\" + fi.Name.Substring(0, 100) + String.Format("_{0:X}", fi.Name.GetHashCode()) +  ((fi.Extension.Length > 10) ? fi.Extension.Substring(0, 10) :
                                                                                                                                                                     fi.Extension);
                string metapath = fi.DirectoryName + "\\" + fi.Name.Substring(0, 100) + String.Format("_{0:X}", fi.Name.GetHashCode()) + ".meta";

                try
                {
                    MoveFileEx("\\\\?\\" + oldname, "\\\\?\\" + newname, MoveFileFlags.MOVEFILE_REPLACE_EXISTING);
                }
                catch
                {
                    Console.WriteLine($"Cannot rename file {oldname}");
                }

                try
                {
                    using (var meta = new StreamWriter(metapath))
                    {
                        meta.WriteLine(oldname);
                    }
                }
                catch { };


                entry = new FileInfo(newname);
                _archiver.AddToBatch(new FileInfo(metapath));
            }

        }
        public bool IsValidFileOrDirectory(ref FileSystemInfo entry)
        {
            if (_settings.FixLongPath) CheckAndFixLength(ref entry);

            string fullname = entry.FullName.ToLower();
            if (fullname.Length == 3) return true;//drive root

            foreach (string word in _settings.BlackList) if (fullname.Contains(word)) return false;

            if ((entry.Attributes & FileAttributes.ReparsePoint) > 0) return false; //reparse - possible symlink
            if ((entry.Attributes & FileAttributes.Hidden) > 0) return false; //skip hidden

            //Вроде все проверки
            //М.б. что-то еще??
            return true;
        }
    }
    class Archiver
    {
        private List<string> _filesArchived;
        private List<string> _filesBatch;
        private string outputPath;
        private StreamWriter output;
        private int filenamePos7zOutput;
        private long _batchSizeCurrent = 0;
        private string _archiveTemplate;
        private enum _reader7zState
        {
            LookingForFileListStart,
            ParsingFileList,
            SkipAll
        }

        private readonly Settings _settings;
        public Archiver(Settings settings)
        {
            _settings = settings;
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
        private bool IsArchivedAlready(FileInfo file)
        {
            //Перевод времени
            //Хз как 7z считает время файла, лень смотреть исходник
            //file.LastWriteTime.IsDaylightSavingTime - показания расходятся с 7z в архиве!
            string filerec0 = file.FullName.Substring(3).ToLower() + " " + file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
            string filerec1 = file.FullName.Substring(3).ToLower() + " " + file.LastWriteTime.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");

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
            if (!zip7.StartInfo.Arguments.Contains("-mmt=")) {resultingArgs += String.Format(" -mmt={0}", Environment.ProcessorCount > 1 ? Environment.ProcessorCount - 1 : 1); }

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
        public void AddToBatch(FileInfo file)
        {
            if (IsArchivedAlready(file)) return;

            _filesBatch.Add(file.FullName);
            _batchSizeCurrent += file.Length;
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
    class WalkerTreeFile
    {
        private readonly Validator _validator;
        private readonly Archiver _archivator;
        private readonly Settings _settings;
        private bool _IgnoreProfileDir;
        public WalkerTreeFile(Settings settings)
        {
            _archivator = new Archiver(settings);
            _validator = new Validator(settings, _archivator);
            _settings = settings;
        }
        private void ProcessDirectory(DirectoryInfo dir)
        {
            FileSystemInfo entry = dir;
            if ((_IgnoreProfileDir) && entry.FullName.StartsWith("C:\\Users", StringComparison.InvariantCultureIgnoreCase)) return;

            if (!_validator.IsValidFileOrDirectory(ref entry)) return;
            dir = (DirectoryInfo)entry;

            FileInfo[] file_list = { };
            DirectoryInfo[] dir_list = { };
            try
            {
                file_list = dir.GetFiles();
                dir_list = dir.GetDirectories();
            }
            catch (Exception ex)
            {
                if (ex is DirectoryNotFoundException) { Console.WriteLine($"Directory not found {dir.FullName}"); return; }
                if ((ex is UnauthorizedAccessException) || (ex is System.Security.SecurityException)) { Console.WriteLine($"Cannot access {dir.FullName}"); return; }
                throw;
            }

            foreach (FileInfo f in file_list) { ProcessFile(f); }
            foreach (DirectoryInfo d in dir_list) { ProcessDirectory(d); }
        }
        private void ProcessFile(FileInfo file)
        {
            FileSystemInfo entry = file;
            if ((_IgnoreProfileDir) && file.FullName.StartsWith("C:\\Users\\", StringComparison.InvariantCultureIgnoreCase)) return;
            if (!_validator.IsValidFileOrDirectory(ref entry)) return;
            file = (FileInfo)entry;

            _archivator.AddToBatch(file);
        }
        public void WalkOverDrive(DirectoryInfo root)
        {
            _IgnoreProfileDir = _settings.IgnoreProfileDir;
            _archivator.Initialize(_settings.DestinationPath + "\\" + "drive_" + root.FullName.Substring(0, 1) + "_");
            ProcessDirectory(root);
            _archivator.Finnalize();
        }
        public void WalkOverProfile(UserProfile profile)
        {
            string metapath = _settings.DestinationPath + "\\profile_" + profile.name.ToLower() + ".meta";

            StreamWriter file = new StreamWriter(metapath);
            file.WriteLine($"Domain: {profile.domain}");
            file.WriteLine($"Name: {profile.name}");
            file.WriteLine($"FullName: {profile.fullname}");
            file.WriteLine($"ProfilePath: {profile.profilepath}");
            file.WriteLine($"LastUsetime: {profile.lastusetime}");
            file.Close();

            _IgnoreProfileDir = false;
            _archivator.Initialize(_settings.DestinationPath + "\\" + "profile_" + profile.name.ToLower() + "_");
            ProcessDirectory(new DirectoryInfo(profile.profilepath));
            _archivator.Finnalize();
        }
        public void WalkOverCustomPath()
        {
            string ident = String.Format("{0:X}" ,_settings.SpecificPath.GetHashCode());
            DirectoryInfo di = new DirectoryInfo(_settings.SpecificPath + "\\");

            bool isRoot = di.FullName.Equals(di.Root.FullName);
            string basePath = isRoot ? _settings.DestinationPath + "\\" + "archive_drive_" + di.Name.Substring(0, 1).ToLower() + "_" + ident:
                                       _settings.DestinationPath + "\\" + "archive_" + di.Name.ToLower() + "_" + ident;


            string metapath = basePath + ".meta";
            
            StreamWriter file = new StreamWriter(metapath);
            file.WriteLine(_settings.SpecificPath);
            file.Close();

            _IgnoreProfileDir = _settings.IgnoreProfileDir;
            _archivator.Initialize(basePath + "_");
            ProcessDirectory(di);
            _archivator.Finnalize();
        }
    }
    enum BackupMode
    {
        Both,
        OnlyProfiles,
        OnlyDrives,
        SpecificPath
    }
    class Settings
    {
        public readonly List<string> BlackList;
        public readonly bool FixLongPath;
        public readonly string DestinationPath;
        public readonly long BatchSizeMax;
        public readonly string Additional7zArgs;
        public readonly string Args7z;
        public readonly string Path7z;
        public readonly BackupMode WorkMode;
        public readonly string SpecificPath;
        public readonly string WorkingDirectory;
        public readonly bool IgnoreProfileDir;
        public Settings(string[] args)
        {
            //Получаем значения по-умолчанию из .config
            //Blacklist
            BlackList = new List<string>();
            var stringCollection = Properties.Settings.Default["BlackList"] as StringCollection;
            foreach (string s in stringCollection) BlackList.Add(s.ToLower());

            //blacklist.Add(((string)Properties.Settings.Default["destination"]).ToLower());

            //FixLongPath
            FixLongPath = (bool)Properties.Settings.Default["FixLongPath"];

            //DestinationPath
            DestinationPath = (string)Properties.Settings.Default["DestinationPath"];

            //BatchSizeMax
            BatchSizeMax = (long)Properties.Settings.Default["BatchSizeMax"];

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
                    WorkMode = BackupMode.Both;
                    break;
                case "profiles":
                    WorkMode = BackupMode.OnlyProfiles;
                    break;
                case "drives":
                    WorkMode = BackupMode.OnlyDrives;
                    break;
                default:
                    WorkMode = BackupMode.SpecificPath;
                    SpecificPath = workmodeRaw;//nonlowercased
                    break;
            }
            //Другие настройки
            WorkingDirectory = Directory.GetCurrentDirectory().ToLower();
            IgnoreProfileDir = false;

            //Парсим аргументы и меняем настройки
            int arg_pos = 0;
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
                        Console.WriteLine($" -FixLongPath {FixLongPath}\nПробовать исправлять длинные(>250 символов) пути\nПереименовывает файл до короткого пути и кладет оригинальное имя в файл .meta рядом с коротким файлом\n");
                        Console.WriteLine($" -bs -BatchSizeMax {BatchSizeMax}\nРазмер файлов в пакете для упаковки в отдельный архив\nНапример -BatchSizeMax 100M\n         -BatchSizeMax 10G\n");
                        Console.WriteLine($" -Path7z {Path7z}\nПуть до 7z.exe\n");
                        Console.WriteLine($" -Additional7zArgs {Additional7zArgs}\nДополнительные параметры коммандной строки 7z\n");
                        Console.WriteLine($" -b -Backup [Both | Profiles | Drives | %path%]\nБекапить всё, только профили, только диски(без папки Users) или указанный путь");
                        Console.WriteLine($" -i {IgnoreProfileDir}\nИгнорировать :\\Users");
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
                        switch (_sizeRaw[len-1])
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
                        BatchSizeMax = long.Parse(_sizeRaw) * multiplier;
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
                        FixLongPath = bool.Parse(args[arg_pos + 1]);
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
                                WorkMode = BackupMode.Both;
                                break;
                            case "profiles":
                                WorkMode = BackupMode.OnlyProfiles;
                                break;
                            case "drives":
                                WorkMode = BackupMode.OnlyDrives;
                                break;
                            default:
                                WorkMode = BackupMode.SpecificPath;
                                SpecificPath = args[arg_pos + 1];//nonlowercased
                                break;
                        }
                        arg_pos++;
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
    }
    class Program
    {
        static public List<UserProfile> GetValidProfiles(DateTime validDate)
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

                    foreach(Domain domname in dom.Forest.Domains)
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
        static void Main(string[] args)
        {
            Settings settings = new Settings(args);
            WalkerTreeFile wtf = new WalkerTreeFile(settings);
            Directory.CreateDirectory(settings.DestinationPath);

            if ((settings.WorkMode == BackupMode.Both) || (settings.WorkMode == BackupMode.OnlyProfiles))
            {
                foreach(UserProfile profile in GetValidProfiles(DateTime.Now.AddYears(-1)))
                {
                    wtf.WalkOverProfile(profile);
                }
            }
            if ((settings.WorkMode == BackupMode.Both) || (settings.WorkMode == BackupMode.OnlyDrives))
            {
                DriveInfo[] allDrives = DriveInfo.GetDrives();
                foreach (DriveInfo drive in allDrives)
                {
                    if ( (drive.IsReady) && (drive.DriveType == DriveType.Fixed) ) wtf.WalkOverDrive(drive.RootDirectory);
                }
            }
            if (settings.WorkMode == BackupMode.SpecificPath)
            {
                wtf.WalkOverCustomPath();
            }
        }

    }
}
