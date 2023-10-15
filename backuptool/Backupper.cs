using System;
using System.Collections.Generic;
using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices;
using System.IO;
using System.Linq;
using System.Management;

namespace backuptool
{
    internal class Backupper
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
}
