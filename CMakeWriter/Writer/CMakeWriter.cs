using System;
using System.Collections.Generic;
using System.Text;

namespace CMakeWriter.Writer
{
    public class CMakeWriter : ICMakeWriter
    {
        public string Platform { get; set; }
        public string[] TargetConfigurations { get; set; }
    }
}
