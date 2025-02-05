﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

using VSslnToCMake;

using EnvDTE;
using Microsoft.VisualStudio.VCProjectEngine;

namespace CMakeWriter.CMProject
{
    public class CMProject
    {
        /// <summary>
        /// Target platform
        /// </summary>
        public string Platform { get; set; }

        /// <summary>
        /// Target configurations
        /// </summary>
        public string[] BuildConfigurations { get; set; }

        /// <summary>
        /// Target name
        /// </summary>
        public string TargetName { get { return targetName; } }

        /// <summary>
        /// Type of output this project generates.
        /// </summary>
        public ConfigurationTypes ConfigurationType { get { return vcCfgs[0].ConfigurationType; } }

        public Project Project { get { return project; } }

        private Logger logger = new NullLogger();
        // Key: Configuration name to be convert
        // Value: Output configuration name
        private Dictionary<string, string> solutionConfigurationNames;
        private Project project;
        private VCProject vcProject;
        private List<VCConfiguration> vcCfgs;
        private SettingsPerConfig projectSettingsPerConfig;
        private List<CMFile> srcs;
        private List<CMFile> hdrs;
        private List<CMFile> resources;
        private string cmakeListsDir;
        private string targetName;

        public CMProject(EnvDTE.Project project)
        {
            Platform = "x64";
            this.project = project;
            this.vcProject = project.Object as VCProject;
        }

        public void setLogger(Logger logger)
        {
            this.logger = logger;
        }

        public void SetSolutionConfigurationName(
            string projectConfigurationName, string solutionConfigurationName)
        {
            if (BuildConfigurations == null)
            {
                return;
            }
            if (BuildConfigurations.Contains(projectConfigurationName))
            {
                if (solutionConfigurationNames == null)
                {
                    solutionConfigurationNames = new Dictionary<string, string>();
                }
                solutionConfigurationNames[projectConfigurationName] =
                    solutionConfigurationName;
            }
        }

        public Dictionary<string, string> getOutputPaths()
        {
            return vcCfgs.ToDictionary(
                x => solutionConfigurationNames[x.ConfigurationName],
                x => x.Evaluate(x.PrimaryOutput));
        }

        public Dictionary<string, string> getImportLibraries()
        {
            return vcCfgs.ToDictionary(
                x => solutionConfigurationNames[x.ConfigurationName],
                x => x.Evaluate(x.ImportLibrary));
        }

        public bool Prepare()
        {
            targetName = project.Name;

            // Directory to output CMakeLists.txt
            cmakeListsDir = System.IO.Path.GetDirectoryName(project.FullName);
            cmakeListsDir = ModifyPath(cmakeListsDir);

            // Target configurations
            var cfgs = vcProject.Configurations as IVCCollection;
            if (BuildConfigurations == null)
            {
                vcCfgs = cfgs.Cast<VCConfiguration>().Where(
                    x => ((dynamic)x.Platform).Name == Platform).ToList();
                BuildConfigurations =
                    vcCfgs.Select(x => x.ConfigurationName).ToArray();
            }
            else
            {
                vcCfgs = new List<VCConfiguration>();
                foreach (var buildConfig in BuildConfigurations)
                {
                    var name = buildConfig + "|" + Platform;
                    var cfg = cfgs.Item(name);
                    if (cfg == null)
                    {
                        OutputError($"Project '{project.Name}' does not contain the configuration '{name}'.");
                        return false;
                    }
                    vcCfgs.Add(cfg as VCConfiguration);
                }
            }

            // Output configuration names
            // If the output configuration names are not set,
            // they are the same as the build configuration names.
            if (solutionConfigurationNames == null)
            {
                solutionConfigurationNames =
                    BuildConfigurations.ToDictionary(x => x, x => x);
            }
            else
            {
                foreach (var cfgName in BuildConfigurations)
                {
                    if (!solutionConfigurationNames.ContainsKey(cfgName))
                    {
                        solutionConfigurationNames.Add(cfgName, cfgName);
                    }
                }
            }
#if DEBUG
            Trace.WriteLine("--- Build Configurations ---");
            Trace.Indent();
            vcCfgs.ForEach(x => Trace.WriteLine(x.Name));
            Trace.Unindent();
#endif

            // Make sure that the type of output are same in all configurations
            if (vcCfgs.Select(x => x.ConfigurationType).Distinct().Count() != 1)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Mismatch the type of output:");
                foreach (var vcCfg in vcCfgs)
                {
                    switch (vcCfg.ConfigurationType)
                    {
                        case ConfigurationTypes.typeApplication:
                            sb.AppendLine($"executable ({vcCfg.Name})");
                            break;
                        case ConfigurationTypes.typeDynamicLibrary:
                            sb.AppendLine($"dynamic link library ({vcCfg.Name})");
                            break;
                        case ConfigurationTypes.typeStaticLibrary:
                            sb.AppendLine($"static link library ({vcCfg.Name})");
                            break;
                        default:
                            break;
                    }
                }
                OutputError("Mismatch the type of output:");
                return false;
            }

            // Make sure that 'Use of MFC' are same in all configurations.
            if (vcCfgs.Select(x => x.useOfMfc).Distinct().Count() != 1)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Mismatch 'Use of MFC':");
                vcCfgs.ForEach(
                    vcCfg =>
                    sb.AppendLine($"  {vcCfg.useOfMfc} ({vcCfg.Name})"));
                OutputError(sb.ToString());
                return false;
            }

            // Target source and header files
            srcs = new List<CMFile>();
            hdrs = new List<CMFile>();
            resources = new List<CMFile>();
            ExtractSourceFiles(project);

            return true;
        }

        public bool Convert(string cmakeSourceDir, List<CMProject> cmProjects)
        {
            logger.Info($"--- Converting {Project.FullName} ---");

            // XML document of the project file to extract settings
            // not provided by COM interfaces.
            var xml = CreateXmlDocumentOfVcxproj();

            projectSettingsPerConfig = (SettingsPerConfig)vcCfgs.ToDictionary(
                vcCfg => vcCfg.ConfigurationName, vcCfg => new Settings());

            ExtractProjectSettings(xml);
            ExtractFileSettings(xml);
            if (WriteCMakeLists(cmakeSourceDir, cmProjects))
            {
                logger.Info($"  {Project.Name} -> {System.IO.Path.Combine(cmakeListsDir, "CMakeLists.txt")}");
            }

            return true;
        }

        private void OutputError(string message)
        {
            System.Console.WriteLine("Error: " + message);
        }

        // Create XmlDocument of the project file.
        // The namespace is removed.
        private System.Xml.XmlDocument CreateXmlDocumentOfVcxproj()
        {
            try
            {
                string text;
                {
                    var xmlOrg = new System.Xml.XmlDocument();
                    xmlOrg.Load(project.FullName);

                    var sw = new StringWriter();
                    var tx = new System.Xml.XmlTextWriter(sw);
                    xmlOrg.WriteTo(tx);
                    text = sw.ToString();
                }

                // Remove namespace
                var sr = new StringReader(text);
                var xmlReader = new System.Xml.XmlTextReader(sr);
                xmlReader.Namespaces = false;

                var xml = new System.Xml.XmlDocument();
                xml.Load(xmlReader);
                xml.DocumentElement.RemoveAttribute("xmlns");

                return xml;
            }
            catch (System.Xml.XmlException e)
            {
                OutputError($"Failed to load {project.FullName}");
                OutputError(e.Message);
            }
            return null;
        }

        private void ExtractSourceFiles(Project vcprj)
        {
            Trace.WriteLine("--- Target source and header files ---");
            Trace.Indent();
            foreach (ProjectItem item in vcprj.ProjectItems)
            {
                ExtractSourceFiles(item);
            }
            Trace.Unindent();
        }

        private void ExtractSourceFiles(ProjectItem item)
        {
            VCFile vcfile = item.Object as VCFile;
            if (vcfile != null)
            {
                switch (vcfile.FileType)
                {
                    case eFileType.eFileTypeCppCode:
                        srcs.Add(new CMFile { vcFile = vcfile });
                        Trace.WriteLine(vcfile.Name);
                        break;
                    case eFileType.eFileTypeCppHeader:
                        hdrs.Add(new CMFile { vcFile = vcfile });
                        Trace.WriteLine(vcfile.Name);
                        break;
                    case eFileType.eFileTypeBMP:
                    case eFileType.eFileTypeICO:
                    case eFileType.eFileTypeRC:
                        resources.Add(new CMFile { vcFile = vcfile });
                        Trace.WriteLine(vcfile.Name);
                        break;
                    default:
                        Trace.WriteLine($"Skipped: {vcfile.Name}");
                        break;
                }
            }
            foreach (ProjectItem subitem in item.ProjectItems)
            {
                ExtractSourceFiles(subitem);
            }
        }

        private void ExtractProjectSettings(System.Xml.XmlDocument xml)
        {
            foreach (var vcCfg in vcCfgs)
            {
                var settings = projectSettingsPerConfig[vcCfg.ConfigurationName];
                VCCLCompilerTool ctool = ((dynamic)vcCfg.Tools).Item("VCCLCompilerTool");

                // Additional include directories
                settings.addIncDirs = ctool.AdditionalIncludeDirectories
                                           .Split(';').ToList();
                TraceSettings("Additional include directories", vcCfg,
                              settings.addIncDirs);

                // Character set
                settings.preprocessorDefs = new List<string>();
                switch (vcCfg.CharacterSet)
                {
                    case charSet.charSetMBCS:
                        settings.preprocessorDefs.Add("_MBCS");
                        break;
                    case charSet.charSetUnicode:
                        settings.preprocessorDefs.Add("_UNICODE");
                        break;
                    case charSet.charSetNotSet:
                    default:
                        break;
                }

                // MFC
                if (vcCfg.useOfMfc == useOfMfc.useMfcDynamic ||
                    vcCfg.useOfMfc == useOfMfc.useMfcStatic)
                {
                    settings.preprocessorDefs.Add("_AFXDLL");
                }

                // Preprocessor definitions
                settings.preprocessorDefs.AddRange(
                    ctool.PreprocessorDefinitions.Split(';').ToList());
                TraceSettings("Preprocessor definitions", vcCfg,
                              settings.preprocessorDefs);

                // Precompiled header file
                settings.pch = new PchSetting
                {
                    use = ctool.UsePrecompiledHeader,
                    pchFilePath = ctool.PrecompiledHeaderFile,
                    headerFilePath = ctool.PrecompiledHeaderThrough
                };

                // Linked libraries
                VCLinkerTool linkerTool = ((dynamic)vcCfg.Tools).Item("VCLinkerTool");
                settings.addLibDirs =
                    linkerTool.AdditionalLibraryDirectories.Split(';').ToList();
                TraceSettings("Additional library directories", vcCfg,
                              settings.addLibDirs);
                settings.linkLibs =
                    linkerTool.AdditionalDependencies.Split(' ').ToList();
                settings.linkLibs.Remove("");
                TraceSettings("Link libraries", vcCfg,
                              settings.linkLibs);

                // SDL check
                var condition = $"'$(Configuration)|$(Platform)'=='{vcCfg.Name}'";
                var nodes = xml.SelectNodes(
                    $"//ItemDefinitionGroup[@Condition=\"{condition}\"]/ClCompile/SDLCheck");
                if (nodes.Count == 1)
                {
                    settings.sdlCheck = nodes[0].InnerText == "true";
                }

                // Minimal rebuild
                settings.minimalRebuild = ctool.MinimalRebuild;

                // MP
                IVCCollection rules = vcCfg.Rules;
                IVCRulePropertyStorage p = (IVCRulePropertyStorage)rules.Item("CL");
                string mp = p.GetEvaluatedPropertyValue("MultiProcessorCompilation");
                if (mp == "true")
                {
                    settings.mp = true;
                }
                else if (mp == "false")
                {
                    settings.mp = false;
                }
            }
        }

        private void ExtractFileSettings(System.Xml.XmlDocument xml)
        {
            srcs.ForEach(x => ExtraceSourceFileSettings(x, xml));
        }

        private void ExtraceSourceFileSettings(CMFile srcFile,
                                               System.Xml.XmlDocument xml)
        {
            var fileCfgs = srcFile.vcFile.FileConfigurations as IVCCollection;
            foreach (string configName in BuildConfigurations)
            {
                var vcCfg = vcCfgs.Find(x => x.ConfigurationName == configName);
                Debug.Assert(vcCfg != null);

                VCFileConfiguration vcFileCfg = (VCFileConfiguration)
                    fileCfgs.Item(configName + "|" + Platform);
                Debug.Assert(vcFileCfg != null);

                var settings = new Settings();
                srcFile.settingsPerConfig.Add(configName, settings);

                VCCLCompilerTool ctool = (VCCLCompilerTool)vcFileCfg.Tool;

                // Preprocessor definitions
                settings.preprocessorDefs =
                    ctool.PreprocessorDefinitions.Split(';').ToList();
                settings.preprocessorDefs.Remove("");

                // Precompiled header file
                settings.pch = new PchSetting
                {
                    use = ctool.UsePrecompiledHeader,
                    pchFilePath = ctool.PrecompiledHeaderFile,
                    headerFilePath = ctool.PrecompiledHeaderThrough
                };

                // SDL Check
                var condition = $"'$(Configuration)|$(Platform)'=='{vcCfg.Name}'";
                var nodes = xml.SelectNodes(
                    $"/Project/ItemGroup/ClCompile[@Include=\"{srcFile.vcFile.Name}\"]/SDLCheck[@Condition=\"{condition}\"]");
                if (nodes.Count == 1)
                {
                    settings.sdlCheck = nodes[0].InnerText == "true";
                }

                // Minimal rebuild
                settings.minimalRebuild = ctool.MinimalRebuild;

                // MP
                var p = vcFileCfg.Tool as IVCRulePropertyStorage;
                string mp = p.GetEvaluatedPropertyValue("MultiProcessorCompilation");
                if (mp == "true")
                {
                    settings.mp = true;
                }
                else if (mp == "false")
                {
                    settings.mp = false;
                }
            }
        }

        private bool WriteCMakeLists(string solutionDir,
                                     List<CMProject> cmProjects)
        {
            // Make sure that the type of output are same in all configurations
            if (vcCfgs.Select(x => x.ConfigurationType).Distinct().Count() != 1)
            {
                var sbError = new StringBuilder();
                sbError.AppendLine("Mismatch the type of output:");
                foreach (var vcCfg in vcCfgs)
                {
                    switch (vcCfg.ConfigurationType)
                    {
                        case ConfigurationTypes.typeApplication:
                            sbError.Append($"  executable");
                            break;
                        case ConfigurationTypes.typeDynamicLibrary:
                            sbError.Append($"  dynamic link library");
                            break;
                        case ConfigurationTypes.typeStaticLibrary:
                            sbError.Append($"  static link library");
                            break;
                        default:
                            break;
                    }
                    sbError.AppendLine($" ({vcCfg.Name})");
                }
                OutputError(sbError.ToString());
                return false;
            }

            var sbh = new StringBuilder(); // For header
            var sb = new StringBuilder();  // For body

            // Project
            sbh.AppendLine($"cmake_minimum_required(VERSION {Constants.CMAKE_REQUIRED_VERSION})");
            sbh.AppendLine();
            sbh.AppendLine($"project({targetName})");
            sbh.AppendLine();

            // Configuration types
            sb.AppendFormat(
                "set(CMAKE_CONFIGURATION_TYPES \"{0}\"",
                string.Join(";", solutionConfigurationNames.Values));
            sb.AppendLine();
            sb.AppendLine("    CACHE STRING \"Configuration types\" FORCE)");
            sb.AppendLine();

            // MFC
            if (vcCfgs[0].useOfMfc == useOfMfc.useMfcDynamic ||
                vcCfgs[0].useOfMfc == useOfMfc.useMfcStatic)
            {
                sb.AppendLine("# Use of MFC");
                sb.AppendFormat($@"set(CMAKE_MFC_FLAG {(vcCfgs[0].useOfMfc == useOfMfc.useMfcStatic ? 1 : 2)})");
                sb.AppendLine();
                sb.AppendLine();
            }

            // Source file names
            switch (vcCfgs[0].ConfigurationType)
            {
                case ConfigurationTypes.typeApplication:
                    sb.AppendLine($"add_executable({targetName}");
                    VCLinkerTool linkerTool = (VCLinkerTool)((dynamic)vcCfgs[0].Tools).Item("VCLinkerTool");
                    if (linkerTool.SubSystem == subSystemOption.subSystemWindows)
                    {
                        sb.AppendLine("  WIN32");
                    }
                    break;
                case ConfigurationTypes.typeDynamicLibrary:
                    sb.AppendLine($"add_library({targetName} SHARED");
                    break;
                case ConfigurationTypes.typeStaticLibrary:
                    sb.AppendLine($"add_library({targetName} STATIC");
                    break;
            }

            var outputFiles = srcs.Select(x => x.vcFile.RelativePath).ToList();
            outputFiles.AddRange(
                hdrs.Select(x => x.vcFile.RelativePath).ToList());
            outputFiles.AddRange(
                resources.Select(x => x.vcFile.RelativePath).ToList());
            outputFiles.Sort();
            outputFiles.ForEach(x => sb.AppendLine($"  {ModifyPath(x)}"));
            sb.AppendLine(")");

            string code;
            var envVars = new List<string>();

            // Output file name
            code = BuildOutputFileNamesString();
            if (code != "")
            {
                sb.AppendLine("# Output file name");
                sb.AppendLine(code);
            }

            // Additional include directories
            code = BuildAdditionalIncludeDirectoriesString(solutionDir);
            if (code != "")
            {
                sb.AppendLine("# Additional include directories");
                sb.AppendLine(code);
                envVars.AddRange(ExtractEnvironmentalVariables(code));
            }

            // Preprocessor definitions
            code = BuildPreprocessorDefinitionsString();
            if (code != "")
            {
                sb.AppendLine("# Preprocessor definitions");
                sb.AppendLine(code);
            }

            // SDL check
            code = BuildSDLCheckString();
            if (code != "")
            {
                sb.AppendLine("# SDL check");
                sb.AppendLine(code);
            }

            // Minimal rebuild
            code = BuildMinimalRebuildString();
            if (code != "")
            {
                sb.AppendLine("# Minimal rebuild");
                sb.AppendLine(code);
            }

            // MP
            // Note.
            //   If the MP option is output before PCH setting,
            //   PCH setting will be lost.
            code = BuildMPString();
            if (code != "")
            {
                sb.AppendLine("# Multi-processor compilation");
                sb.AppendLine(code);
            }

            // Precompiled header
            code = BuildPrecompiledHeaderString();
            if (code != "")
            {
                sb.AppendLine("# Precompiled header files");
                sb.AppendLine(code);
            }

            // Linked libraries
            code = BuildAdditionalLibraryDirectoriesString(solutionDir);
            if (code != "")
            {
                sb.AppendLine("# Additional library directories");
                sb.AppendLine(code);
                envVars.AddRange(ExtractEnvironmentalVariables(code));
            }
            code = BuildLinkedLibrariesString(cmProjects);
            if (code != "")
            {
                sb.AppendLine("# Link libraries");
                sb.AppendLine(code);
            }

            // Write CMakeLists.txt.
            if (envVars.Count > 0)
            {
                sbh.AppendFormat("foreach (EnvVar IN ITEMS {0})",
                                 string.Join(" ", envVars.Distinct()));
                sbh.AppendLine();
                sbh.AppendLine("  if (\"$ENV{${EnvVar}}\" STREQUAL \"\")");
                sbh.AppendLine("    message(WARNING, \"Environmental variable '${EnvVar}' is not defined.\")");
                sbh.AppendLine("  endif ()");
                sbh.AppendLine("endforeach ()");
            }

            var cmakeListsPath =
                System.IO.Path.Combine(cmakeListsDir, "CMakeLists.txt");
            var sw = new System.IO.StreamWriter(cmakeListsPath);
            sw.Write(sbh.ToString());
            sw.Write(sb.ToString().Trim());
            sw.WriteLine();
            sw.Close();

            return true;
        }

        private string BuildOutputFileNamesString()
        {
            if (vcCfgs.All(
                    vcCfg =>
                    {
                        var fileName = Path.GetFileNameWithoutExtension(vcCfg.PrimaryOutput);
                        return fileName == targetName;
                    }))
            {
                return "";
            }

            var sb = new StringBuilder();
            sb.AppendFormat($"set_target_properties({targetName}");
            sb.AppendLine();
            sb.AppendFormat("  PROPERTIES");
            sb.AppendLine();
            foreach (var vcCfg in vcCfgs)
            {
                var configUpper = solutionConfigurationNames[vcCfg.ConfigurationName].ToUpper();
                var fileName = System.IO.Path.GetFileNameWithoutExtension(vcCfg.PrimaryOutput);
                sb.AppendFormat($"  OUTPUT_NAME_{configUpper} {fileName}");
                sb.AppendLine();
            }
            sb.AppendLine(")");

            return sb.ToString();
        }

        private string GetSolutionConfigurationName<Value>(KeyValuePair<string, Value> kv)
        {
            return solutionConfigurationNames[kv.Key];
        }

        private string GetSolutionConfigurationName<Value>((string, Value) kv) => solutionConfigurationNames[kv.Item1];

        private string GetSolutionConfigurationName(string name) => solutionConfigurationNames[name];

        private string BuildAdditionalIncludeDirectoriesString(
            string solutionDir)
        {
            solutionDir = ModifyPath(solutionDir);

            // Additional include directories per configuration
            var dirsPerCfg = new List<(string cfgName, List<string> paths)>();
            foreach (var kvp in projectSettingsPerConfig)
            {
                var vcCfg = vcCfgs.Find(x => x.ConfigurationName == kvp.Key);
                Debug.Assert(vcCfg != null);
                var settings = kvp.Value;
                var paths = EvaluateDirectories(settings.addIncDirs, vcCfg,
                                                solutionDir);
                dirsPerCfg.Add((vcCfg.ConfigurationName, paths));
            }

            if (dirsPerCfg.All(x => x.paths.Count == 0))
            {
                return "";
            }

            // Build a string.
            var sb = new StringBuilder();
            sb.AppendLine($"set_property(TARGET {targetName}");
            sb.AppendLine($"  APPEND PROPERTY INCLUDE_DIRECTORIES");
            sb.AppendFormat("  {0}",
                dirsPerCfg.ConfigExpressions(
                    Environment.NewLine + "  ",
                    GetSolutionConfigurationName,
                    kv => Environment.NewLine + "    " +
                          string.Join(";" + Environment.NewLine + "    ",
                                      kv.paths)));
            sb.AppendLine();
            sb.AppendLine(")");

            return sb.ToString();
        }

        private string BuildPreprocessorDefinitionsString()
        {
            var sb = new StringBuilder();
            var cfgNames = projectSettingsPerConfig.Select(kv => kv.Key);

            // Project settings
            sb.AppendLine($"target_compile_definitions({targetName} PRIVATE");
            sb.AppendFormat("  {0}",
                cfgNames.ConfigExpressions(
                    Environment.NewLine + "  ", GetSolutionConfigurationName,
                    (cfgName) => string.Join(";", projectSettingsPerConfig[cfgName].preprocessorDefs)));
            sb.AppendLine();
            sb.AppendLine(")");

            // Keep project settings and sort them to compare ones of each file.
            var orderedPpDefs = new Dictionary<string, List<string>>();
            foreach (var cfgName in cfgNames)
            {
                var settings = projectSettingsPerConfig[cfgName];
                var ppDefs = new List<string>(settings.preprocessorDefs);
                ppDefs.Sort();
                orderedPpDefs.Add(cfgName, ppDefs);
            }

            // File settings
            foreach (var src in srcs)
            {
                if (src.settingsPerConfig.All(
                        kv => kv.Value.preprocessorDefs.Count == 0))
                {
                    continue;
                }
                if (cfgNames.All(x => {
                    var settings = src.settingsPerConfig[x];
                    var ppDefs = new List<string>(settings.preprocessorDefs);
                    ppDefs.Sort();
                    return ppDefs.SequenceEqual(orderedPpDefs[x]);
                }))
                {
                    continue;
                }

                var filePath = Utility.ToRelativePath(src.vcFile.FullPath,
                                                      cmakeListsDir);
                sb.AppendFormat($"set_property(SOURCE {filePath}");
                sb.AppendLine();
                sb.AppendLine("  APPEND_STRING PROPERTY COMPILE_FLAGS");
                sb.AppendFormat(
                    "  \"{0}\")",
                    BuildConfigurationExpressions(
                        src.settingsPerConfig
                            .Where(kv => kv.Value.preprocessorDefs.Count() > 0)
                            .Select(kv => (
                                solutionConfigurationNames[kv.Key],
                                string.Join(
                                    ";", kv.Value.preprocessorDefs.Select(pp => "-D" + pp))))));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string BuildSDLCheckString()
        {
            var sb = new StringBuilder();

            // Project settings
            var cfgNames = projectSettingsPerConfig
                           .Where(kv => kv.Value.sdlCheck != null)
                           .Select(kv => kv.Key);
            if (cfgNames.Count() > 0)
            {
                sb.AppendLine($"target_compile_options({targetName} PRIVATE");
                sb.AppendFormat(
                    "  \"{0}\"",
                    cfgNames.ConfigExpressions(
                        "\"" + System.Environment.NewLine + "  \"",
                        GetSolutionConfigurationName,
                        cfgName => {
                            bool b = (bool)projectSettingsPerConfig[cfgName].sdlCheck;
                            return b ? "/sdl" : "/sdl-";
                        }));
                sb.AppendLine();
                sb.AppendLine(")");
            }

            // File settings
            foreach (var src in srcs)
            {
                if (src.settingsPerConfig.Values.All(x => x.sdlCheck == null))
                {
                    continue;
                }
                var filePath = Utility.ToRelativePath(src.vcFile.FullPath,
                                                      cmakeListsDir);
                sb.AppendLine($"set_property(SOURCE {filePath}");
                sb.AppendLine("  APPEND_STRING PROPERTY COMPILE_FLAGS");
                foreach (var kv in src.settingsPerConfig)
                {
                    if (kv.Value.sdlCheck != null)
                    {
                        sb.AppendFormat(
                            "  \"$<$<CONFIG:{0}>:{1}>\"",
                            solutionConfigurationNames[kv.Key],
                            (bool)kv.Value.sdlCheck ? "/sdl" : "/sdl-");
                        sb.AppendLine();
                    }
                }
                sb.AppendLine(")");
            }

            return sb.ToString();
        }

        private string BuildMinimalRebuildString()
        {
            var sb = new StringBuilder();

            // Project settings
            var cfgNames = projectSettingsPerConfig
                           .Where(kv => kv.Value.minimalRebuild != null)
                           .Select(kv => kv.Key);
            if (cfgNames.Count() > 0)
            {
                sb.AppendLine($"target_compile_options({targetName} PRIVATE");
                sb.AppendFormat(
                    "  \"{0}\"",
                    cfgNames.ConfigExpressions(
                        "\"" + System.Environment.NewLine + "  \"",
                        GetSolutionConfigurationName,
                        cfgName => {
                            bool b = (bool)projectSettingsPerConfig[cfgName].minimalRebuild;
                            return b ? "/Gm" : "/Gm-";
                        }));
                sb.AppendLine();
                sb.AppendLine(")");
            }

            // File settings
            foreach (var src in srcs)
            {
                if (src.settingsPerConfig.Values.All(x => x.minimalRebuild == null))
                {
                    continue;
                }
                if (src.settingsPerConfig.All(
                        kv => kv.Value.minimalRebuild == projectSettingsPerConfig[kv.Key].minimalRebuild))
                {
                    continue;
                }
                var filePath = Utility.ToRelativePath(src.vcFile.FullPath,
                                                      cmakeListsDir);
                sb.AppendLine($"set_property(SOURCE {filePath}");
                sb.AppendLine("  APPEND_STRING PROPERTY COMPILE_FLAGS");
                foreach (var kv in src.settingsPerConfig)
                {
                    if (kv.Value.minimalRebuild != null)
                    {
                        sb.AppendFormat(
                            "  \"$<$<CONFIG:{0}>:{1}>\"",
                            solutionConfigurationNames[kv.Key],
                            (bool)kv.Value.minimalRebuild ? "/Gm" : "/Gm-");
                        sb.AppendLine();
                    }
                }
                sb.AppendLine(")");
            }

            var text = sb.ToString();
            if (text == "")
            {
                return "";
            }

            var sb2 = new StringBuilder();
            sb2.AppendLine("if (MSVC)");
            sb2.Append(IndentText(text));
            sb2.AppendLine("endif ()");
            return sb2.ToString();
        }

        private string BuildMPString()
        {
            var sb = new StringBuilder();

            // Project settings

            // Key: Configuration name, Value: MP
            var projectsCfgAndMP = projectSettingsPerConfig
                                   .ToDictionary(kv => kv.Key,
                                                 kv => kv.Value.mp);
            // Remove project's MP option if any file has false MP option.
            var cfgNames = new List<string>(projectsCfgAndMP.Keys);
            foreach (var cfgName in cfgNames)
            {
                if (srcs.Any(src => src.settingsPerConfig[cfgName].mp == false))
                {
                    projectsCfgAndMP[cfgName] = null;
                }
            }

            if (projectsCfgAndMP.Any(kv => kv.Value == true))
            {
                sb.AppendLine($"target_compile_options({targetName} PRIVATE");
                sb.AppendFormat(
                    "  \"{0}\"",
                    projectsCfgAndMP
                        .Where(kv => kv.Value == true)
                        .ConfigExpressions(
                            "\"" + System.Environment.NewLine + "  \"",
                            GetSolutionConfigurationName, kv => "/MP"));
                sb.AppendLine();
                sb.AppendLine(")");
            }

            // File settings
            foreach (var src in srcs)
            {
                cfgNames = src.settingsPerConfig
                           .Where(kv =>
                           {
                               var mp = kv.Value.mp;
                               return mp == true &&
                                      projectsCfgAndMP[kv.Key] != true;
                           })
                           .Select(kv => kv.Key)
                           .ToList();

                if (cfgNames.Count > 0)
                {
                    var filePath = Utility.ToRelativePath(src.vcFile.FullPath,
                                                          cmakeListsDir);
                    sb.AppendLine($"set_property(SOURCE {filePath}");
                    sb.AppendLine("  APPEND_STRING PROPERTY COMPILE_FLAGS");
                    sb.AppendFormat(
                        "  \"{0}\"",
                        cfgNames.ConfigExpressions(
                            "\"" + System.Environment.NewLine + "  \"",
                            GetSolutionConfigurationName, kv => "/MP"));
                    sb.AppendLine();
                    sb.AppendLine(")");
                }
            }

            var text = sb.ToString();
            if (text == "")
            {
                return "";
            }

            var sb2 = new StringBuilder();
            sb2.AppendLine("if (MSVC)");
            sb2.Append(IndentText(text));
            sb2.AppendLine("endif ()");
            return sb2.ToString();
        }

        private string BuildPrecompiledHeaderString()
        {
            string text;
            // If the project uses precompiled headers (PCH),
            // and one of the files does not use PCH,
            // the PCH settings is not output for the project
            // and is output for each file .
            if (projectSettingsPerConfig.Where(kv => kv.Value.pch.use == pchOption.pchUseUsingSpecific).Count() > 0 &&
                srcs.Find(src => src.settingsPerConfig.Where(kv => kv.Value.pch.use == pchOption.pchNone).Count() > 0) != null)
            {
                // Gather file names with the same pch option string.
                var cfgNames = projectSettingsPerConfig.Select(x => x.Key).ToList();
                var pchOptsAndFiles = new List<(List<string> pchOptions,
                                                List<string> fileNames)>();
                foreach (var src in srcs)
                {
                    string filePath = Utility.ToRelativePath(src.vcFile.FullPath,
                                                             cmakeListsDir);
                    var pchSetting = cfgNames.Select(cfgName => src.settingsPerConfig[cfgName].pch.BuildPchOptionString()).ToList();
                    int index = pchOptsAndFiles.FindIndex(
                        kv => kv.pchOptions.SequenceEqual(pchSetting));
                    if (index < 0)
                    {
                        pchOptsAndFiles.Add((pchSetting,
                                             new List<string> { filePath }));
                    }
                    else
                    {
                        pchOptsAndFiles[index].fileNames.Add(filePath);
                    }
                }

                // Build a string.
                var sb = new StringBuilder();
                foreach (var (pchOptions, filePaths) in pchOptsAndFiles)
                {
                    var cfgAndPchList =
                        Enumerable.Range(0, cfgNames.Count())
                        .Where(index => pchOptions[index] != "")
                        .Select(index => (cfgNames[index], pchOptions[index]));
                    if (cfgAndPchList.Count() == 0)
                    {
                        continue;
                    }

                    sb.AppendFormat("set_property(SOURCE {0}",
                                    string.Join(" ", filePaths));
                    sb.AppendLine();
                    sb.AppendLine("  APPEND_STRING PROPERTY COMPILE_FLAGS");
                    sb.AppendFormat(
                        "  \"{0}\")",
                        cfgAndPchList.ConfigExpressions(
                            "\\" + Environment.NewLine + "   ",
                            kv => solutionConfigurationNames[kv.Item1],
                            kv => kv.Item2));
                    sb.AppendLine();
                }

                text = sb.ToString();
            }
            else
            {
                var sb = new StringBuilder();

                // Project settings
                var opt = projectSettingsPerConfig.ConfigExpressions(
                    "\"" + Environment.NewLine + "  \"",
                    GetSolutionConfigurationName,
                    kv => kv.Value.pch.BuildPchOptionString());
                if (opt != "")
                {
                    sb.AppendLine($"target_compile_options({targetName} PRIVATE");
                    sb.AppendLine($"  \"{opt}\"");
                    sb.AppendLine(")");
                }

                // File settings
                foreach (var src in srcs)
                {
                    bool mismatched = src.settingsPerConfig.Any(
                        kv => kv.Value.pch != projectSettingsPerConfig[kv.Key].pch);

                    if (!mismatched)
                    {
                        continue;
                    }

                    opt = BuildConfigurationExpressions(
                        src.settingsPerConfig.Select(
                            kv => (solutionConfigurationNames[kv.Key],
                                   kv.Value.pch.BuildPchOptionString())));
                    if (opt != "")
                    {
                        var filePath = Utility.ToRelativePath(
                            src.vcFile.FullPath, cmakeListsDir);
                        sb.AppendFormat($"set_property(SOURCE {filePath}");
                        sb.AppendLine();
                        sb.AppendLine("  APPEND_STRING PROPERTY COMPILE_FLAGS");
                        sb.AppendLine($"  \"{opt}\")");
                    }
                }

                text = sb.ToString();
            }

            if (text == "")
            {
                return "";
            }

            var sb2 = new StringBuilder();
            sb2.AppendLine("if (MSVC)");
            sb2.Append(IndentText(text));
            sb2.AppendLine("endif ()");
            return sb2.ToString();
        }

        private string BuildConfigurationExpressions(
            IEnumerable<(string cfgName, string text)> data)
        {
            return String.Join(
                " \\" + Environment.NewLine + "   ",
                data.Select(kv => String.Format("$<$<CONFIG:{0}>:{1}>",
                                                kv.cfgName, kv.text)));
        }

        private string BuildAdditionalLibraryDirectoriesString(
            string solutionDir)
        {
            string msvc = BuildAdditionalLibraryDirectoriesString(
                solutionDir, "/LIBPATH:");
            if (msvc == "")
            {
                return "";
            }

            string others = BuildAdditionalLibraryDirectoriesString(
                solutionDir, "-L");
            var sb = new StringBuilder();
            sb.AppendLine("if (MSVC)");
            sb.Append(IndentText(msvc));
            sb.AppendLine("else ()");
            sb.Append(IndentText(others));
            sb.AppendLine("endif ()");
            return sb.ToString();
        }

        private string BuildAdditionalLibraryDirectoriesString(
            string solutionDir, string option)
        {
            solutionDir = Utility.ToUnixPath(solutionDir);

            // Additional library directories per configuration
            var dirsPerCfg = new List<(string cfgName, List<string> paths)>();
            foreach (var kvp in projectSettingsPerConfig)
            {
                var vcCfg = vcCfgs.Find(x => x.ConfigurationName == kvp.Key);
                Debug.Assert(vcCfg != null);
                var settings = kvp.Value;
                var paths = EvaluateDirectories(settings.addLibDirs, vcCfg,
                                                solutionDir);
                dirsPerCfg.Add((vcCfg.ConfigurationName, paths));
            }

            if (dirsPerCfg.All(x => x.paths.Count == 0))
            {
                return "";
            }

            // Build a string.
            var sb = new StringBuilder();
            sb.AppendLine($"target_link_options({targetName} PRIVATE");
            foreach (var (cfgName, paths) in dirsPerCfg)
            {
                if (paths.Count == 0)
                {
                    continue;
                }
                sb.AppendLine($"  $<$<CONFIG:{solutionConfigurationNames[cfgName]}>:");
                foreach (var path in paths)
                {
                    sb.Append($"    {option}{path}");
                    if (paths.IndexOf(path) == paths.Count - 1)
                    {
                        sb.AppendLine(">");
                    }
                    else
                    {
                        sb.AppendLine();
                    }
                }
            }
            sb.AppendLine(")");
            return sb.ToString();
        }

        private List<string> EvaluateDirectories(
            IEnumerable<string> dirs, VCConfiguration vcCfg,
            string solutionDir)
        {
            var slnDir = Utility.ToUnixPath(solutionDir);
            var paths = new List<string>();
            foreach (var pathOrg in dirs)
            {
                string absPath = TranslatePath(pathOrg, vcCfg);
                if (absPath == "")
                {
                    continue;
                }

                string path;
                if (ComparePaths(absPath, cmakeListsDir, cmakeListsDir.Length) == 0)
                {
                    path = "${CMAKE_CURRENT_SOURCE_DIR}/"
                           + Utility.ToRelativePath(absPath, cmakeListsDir);
                }
                else if (ComparePaths(absPath, slnDir, slnDir.Length) == 0)
                {
                    path = "${CMAKE_SOURCE_DIR}/"
                           + Utility.ToRelativePath(
                               Utility.AddTrailingSlashToPath(absPath), slnDir);
                }
                else
                {
                    path = EvaluateVSMacros(pathOrg, vcCfg);
                    path = Regex.Replace(path, @"\$\((.*?)\)", @"$ENV{$1}",
                                         RegexOptions.Singleline);
                }

                path = path.TrimEnd('/');
                path = Utility.AddDoubleQuotesToPath(path);
                paths.Add(path);
            }
            return paths;
        }

        private string BuildLinkedLibrariesString(List<CMProject> cmProjects)
        {
            if (projectSettingsPerConfig.All(kv => kv.Value.linkLibs.Count == 0))
            {
                return "";
            }

            var sb = new StringBuilder();

            sb.AppendFormat($"set_property(TARGET {targetName}");
            sb.AppendLine();
            sb.AppendFormat("  APPEND PROPERTY LINK_LIBRARIES");
            sb.AppendLine();

            foreach (var kv in projectSettingsPerConfig)
            {
                var linkLibs = new List<string>();
                var cfgName = kv.Key;
                var settings = kv.Value;
                var linkOptions = new List<string>();
                foreach (var linkLibOrg in settings.linkLibs)
                {
                    var vcCfg = vcCfgs.Find(x => x.ConfigurationName == cfgName);
                    string linkLib = TranslatePath(linkLibOrg, vcCfg);
                    List<string> linkLibFullPaths;

                    // If a library path is relative, combine an additional
                    // library path and the library path.
                    if (System.IO.Path.IsPathRooted(linkLib))
                    {
                        linkLibFullPaths = new List<string> { linkLib };
                    }
                    else
                    {
                        linkLibFullPaths = settings.addLibDirs.Select(
                            x => System.IO.Path.Combine(vcCfg.Evaluate(x), linkLib)).ToList();
                    }

                    // Find if a linked library path matches the output
                    // file path of ones of any CMProject.
                    CMProject linkProject = null;
                    foreach (var linkLibFullPath in linkLibFullPaths)
                    {
                        foreach (var cmProject in cmProjects)
                        {
                            if (cmProject == this)
                            {
                                continue;
                            }

                            Dictionary<string, string> candidateLibs = null;
                            switch (cmProject.ConfigurationType)
                            {
                                case ConfigurationTypes.typeStaticLibrary:
                                    candidateLibs = cmProject.getOutputPaths();
                                    break;
                                case ConfigurationTypes.typeDynamicLibrary:
                                    candidateLibs = cmProject.getImportLibraries();
                                    break;
                                default:
                                    break;
                            }

                            if (candidateLibs == null)
                            {
                                continue;
                            }

                            if (ComparePaths(linkLibFullPath,
                                             candidateLibs[cfgName]) == 0)
                            {
                                linkProject = cmProject;
                                break;
                            }
                        }
                        if (linkProject != null)
                        {
                            break;
                        }
                    }

                    if (linkProject == null)
                    {
                        linkOptions.Add(TranslatePath(linkLibOrg, vcCfg));
                    }
                    else
                    {
                        linkOptions.Add(linkProject.TargetName);
                    }
                }

                sb.AppendFormat("  \"$<$<CONFIG:{0}>:{1}>\"",
                                solutionConfigurationNames[cfgName],
                                string.Join(";", linkOptions));
                sb.AppendLine();
            }
            sb.AppendLine(")");

            return sb.ToString();
        }

        private static string ModifyPath(string path)
        {
            return path.Replace('\\', '/');
        }

        private static int ComparePaths(string lhs, string rhs)
        {
            lhs = ModifyPath(lhs);
            rhs = ModifyPath(rhs);
            return string.Compare(lhs, rhs, true);
        }

        private static int ComparePaths(string lhs, string rhs, int length)
        {
            lhs = ModifyPath(lhs);
            rhs = ModifyPath(rhs);
            return string.Compare(lhs, 0, rhs, 0, length, true);
        }

        private string TranslatePath(string path, VCConfiguration vcCfg)
        {
            string path1 = vcCfg.Evaluate(path);
            if (path1 == "")
            {
                return "";
            }
            if (System.IO.Path.IsPathRooted(path1))
            {
                path1 = Utility.NormalizePath(path1);
            }
            return ModifyPath(path1);
        }

        private List<string> TranslatePaths(IEnumerable<string> paths,
                                            VCConfiguration vcCfg)
        {
            var results = new List<string>();
            foreach (string path in paths)
            {
                string path1 = TranslatePath(path, vcCfg);
                if (path1 != "")
                {
                    results.Add(path1);
                }
            }
            return results;
        }

        // Macro names defined by Visual Studio.
        // See https://msdn.microsoft.com/library/239bd708-2ea9-4687-b264-043f1febf98b
        private readonly List<string> vsMacroNames = new List<string>{
            "$(RemoteMachine)",
            "$(Configuration)",
            "$(Platform)",
            "$(ParentName)",
            "$(RootNameSpace)",
            "$(IntDir)",
            "$(OutDir)",
            "$(DevEnvDir)",
            "$(InputDir)",
            "$(InputPath)",
            "$(InputName)",
            "$(InputFileName)",
            "$(InputExt)",
            "$(ProjectDir)",
            "$(ProjectPath)",
            "$(ProjectName)",
            "$(ProjectFileName)",
            "$(ProjectExt)",
            "$(SolutionDir)",
            "$(SolutionPath)",
            "$(SolutionName)",
            "$(SolutionFileName)",
            "$(SolutionExt)",
            "$(TargetDir)",
            "$(TargetPath)",
            "$(TargetName)",
            "$(TargetFileName)",
            "$(TargetExt)",
            "$(VSInstallDir)",
            "$(VCInstallDir)",
            "$(FrameworkDir)",
            "$(FrameworkVersion)",
            "$(FrameworkSDKDir)",
            "$(WebDeployPath)",
            "$(WebDeployRoot)",
            "$(SafeParentName)",
            "$(SafeInputName)",
            "$(SafeRootNamespace)",
            "$(FxCopDir)",
            "$(NOINHERIT)"
        };

        /// <summary>
        /// Evaluates macro only defined by Visual Studio.
        /// </summary>
        private string EvaluateVSMacros(string text, VCConfiguration vcCfg)
        {
            if (text == "")
            {
                return "";
            }

            int startIndex = 0;
            while (true)
            {
                int begin = text.IndexOf("$(", startIndex);
                if (begin < 0)
                {
                    break;
                }
                int end = text.IndexOf(")", begin);
                if (end < 0)
                {
                    break;
                }

                string macroName = text.Substring(begin, end - begin + 1);
                int index = vsMacroNames.IndexOf(macroName);
                if (index >= 0)
                {
                    string val = vcCfg.Evaluate(vsMacroNames[index]);
                    text = text.Substring(0, begin) + val + text.Substring(end + 1);
                    startIndex = begin + val.Length;
                }
                else
                {
                    startIndex = end + 1;
                }
            }
            return text;
        }

        /// <summary>
        /// Extracts CMake style of environmental variables from a string.
        /// </summary>
        private static List<string> ExtractEnvironmentalVariables(string text)
        {
            var env = new List<string>();
            var regex = new Regex(@"\$ENV\{(.*?)\}");
            var mc = regex.Matches(text);
            return mc.Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        }

        private static string IndentText(string text)
        {
            var sb = new StringBuilder();
            foreach (var line in text.Split(new string[] { Environment.NewLine },
                                            StringSplitOptions.None))
            {
                if (line != "" && line != Environment.NewLine)
                {
                    sb.AppendLine("  " + line);
                }
            }
            return sb.ToString();
        }

        private void TraceSettings(string name, VCConfiguration vcCfg,
                                   List<string> items)
        {
            Trace.WriteLine($"--- {name} ({vcCfg.Name}) ---");
            Trace.Indent();
            items.ForEach(x => Trace.WriteLine(x));
            Trace.Unindent();
        }
    }
}
