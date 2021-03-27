using System.Collections.Generic;

namespace CMakeWriter.CMProject
{
    public class Settings
    {
        /// <summary>
        /// Additional include directories
        /// </summary>
        public List<string> addIncDirs;

        /// <summary>
        /// Preprocessor definitions
        /// </summary>
        public List<string> preprocessorDefs;

        /// <summary>
        /// Additional library directories
        /// </summary>
        public List<string> addLibDirs;

        /// <summary>
        /// Lib files of Linker
        /// </summary>
        public List<string> linkLibs;

        /// <summary>
        /// Precompiled header file setting
        /// </summary>
        public PchSetting pch;

        /// <summary>
        /// SDL check
        /// </summary>
        public bool? sdlCheck;

        /// <summary>
        /// Minimal rebuild. /Gm
        /// </summary>
        public bool? minimalRebuild;

        /// <summary>
        /// Multi-processor compilation
        /// </summary>
        public bool? mp;
    }
}
