using Microsoft.VisualStudio.VCProjectEngine;

namespace CMakeWriter.CMProject
{
    public class PchSetting
    {
        private const string DEFAULT_PCH_FILE_PATH = "$(IntDir)$(TargetName).pch";
        private const string DEFAULT_HEADER_FILE_PATH = "stdafx.h";

        public pchOption use = pchOption.pchNone;
        public string pchFilePath;
        public string headerFilePath;

        public static bool operator ==(PchSetting lhs, PchSetting rhs)
        {
            return lhs.use == rhs.use &&
                   lhs.pchFilePath == rhs.pchFilePath &&
                   lhs.headerFilePath == rhs.headerFilePath;
        }

        public static bool operator !=(PchSetting lhs, PchSetting rhs)
        {
            return lhs.use != rhs.use ||
                   lhs.pchFilePath != rhs.pchFilePath ||
                   lhs.headerFilePath != rhs.headerFilePath;
        }

        /// <summary>
        /// Builds string of precompiled header option.
        /// </summary>
        public string BuildPchOptionString()
        {
            if (use == pchOption.pchNone)
            {
                return "";
            }

            string option = "";
            if (use == pchOption.pchUseUsingSpecific)
            {
                option += "/Yu";
            }
            else if (use == pchOption.pchCreateUsingSpecific)
            {
                option += "/Yc";
            }
            // TODO デフォルト値の判定
            if (headerFilePath != DEFAULT_HEADER_FILE_PATH)
            {
                option += string.Format($"\"{headerFilePath}\"");
            }
            if (pchFilePath != DEFAULT_PCH_FILE_PATH)
            {
                option += string.Format($" /Fp\"{pchFilePath}\"");
            }
            return option;
        }
    }
}
