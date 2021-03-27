using System;

namespace CMakeWriter.Writer
{
    public interface ICMakeWriter
    {
        /// <summary>
        /// Target platform
        /// </summary>
        public string Platform { get; set; }

        /// <summary>
        /// Target configurations
        /// </summary>
        public string[] TargetConfigurations { get; set; }
    }
}
