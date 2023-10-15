using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;

namespace backuptool
{
    internal class Settings //singleton
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
}
