using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace backuptool
{
    internal class Archiver
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
}
