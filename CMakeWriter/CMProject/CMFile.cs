using Microsoft.VisualStudio.VCProjectEngine;

namespace CMakeWriter.CMProject
{
    public class CMFile
    {
        public VCFile vcFile;
        public SettingsPerConfig settingsPerConfig = new SettingsPerConfig();
    }
}
